using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core;

public class MobileSAMService : IDisposable
{
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private readonly AIResourceService _resourceService;
    private bool _isInitialized = false;

    private DenseTensor<float>? _imageEmbeddings;
    private int _originalWidth;
    private int _originalHeight;
    private int _lastModelTargetSize = 1024;
    private int _lastNewW;
    private int _lastNewH;
    private int _offsetX; // NEW: X offset for center padding
    private int _offsetY; // NEW: Y offset for center padding
    private string _lastIouInfo = "";
    private DenseTensor<float>? _lowResMask;

    public string LastIouInfo => _lastIouInfo;

    public MobileSAMService(AIResourceService resourceService)
    {
        _resourceService = resourceService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Ensure we load the native libraries from the custom path
        _resourceService.SetupNativeResolvers();

        var baseDir = _resourceService.GetAIResourcesPath();
        var encoderPath = Path.Combine(baseDir, "models", "mobile_sam_image_encoder.onnx");
        var decoderPath = Path.Combine(baseDir, "models", "sam_mask_decoder_multi.onnx");

        if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
            throw new FileNotFoundException("MobileSAM models not found. Please download them first.");

        await Task.Run(() =>
        {
            try
            {
                var options = new SessionOptions();
                bool cudaSuccess = false;
                try
                {
                    // Try CUDA first
                    options.AppendExecutionProvider_CUDA(0);
                    System.Diagnostics.Debug.WriteLine("MobileSAM: Using CUDA");
                    cudaSuccess = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MobileSAM: CUDA not available, falling back to CPU: {ex.Message}");
                }

                try
                {
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);
                }
                catch (Exception ex) when (cudaSuccess)
                {
                    System.Diagnostics.Debug.WriteLine($"MobileSAM: Session creation with CUDA failed: {ex.Message}. Retrying with CPU.");
                    options = new SessionOptions(); // Reset options
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);
                }

                _isInitialized = true;
                System.Diagnostics.Debug.WriteLine("MobileSAM: Sessions initialized");
            }
            catch (TypeInitializationException ex)
            {
                var inner = ex.InnerException?.Message ?? "No inner exception";
                throw new Exception($"MobileSAM ONNX Runtime failed to initialize. \nInner Error: {inner}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to initialize MobileSAM models: {ex.Message}", ex);
            }
        });
    }


    public async Task SetImageAsync(byte[] imageBytes)
    {
        if (!_isInitialized) await InitializeAsync();
        if (_encoderSession == null) throw new InvalidOperationException("Encoder session not initialized.");

        await Task.Run(() =>
        {
            using var original = SKBitmap.Decode(imageBytes);
            if (original == null) throw new ArgumentException("Invalid image data.");

            _originalWidth = original.Width;
            _originalHeight = original.Height;

            // Detect TargetSize from model metadata
            var inputName = _encoderSession.InputMetadata.Keys.FirstOrDefault() ?? "image";
            var inputMeta = _encoderSession.InputMetadata[inputName];
            var dims = inputMeta.Dimensions;
            var rank = dims.Length;
            
            // For CHW [1, 3, 1024, 1024] -> TargetSize is dims[2]
            // For HWC [1, 1024, 1024, 3] -> TargetSize is dims[1]
            int modelTargetSize = 1024; // fallback for dynamic axes
            if (rank == 4) 
            {
                int d1 = dims[1]; int d2 = dims[2];
                modelTargetSize = (d1 == 3) ? (d2 > 0 ? d2 : 1024) : (d1 > 0 ? d1 : 1024);
            }
            else if (rank == 3) 
            {
                int d0 = dims[0]; int d1 = dims[1];
                modelTargetSize = (d0 == 3) ? (d1 > 0 ? d1 : 1024) : (d0 > 0 ? d0 : 1024);
            }
            
            System.Diagnostics.Debug.WriteLine($"MobileSAM: Detected model target size: {modelTargetSize}");

            // 1. Resize and Pad (CENTERED)
            _lastModelTargetSize = modelTargetSize;
            float scale = (float)_lastModelTargetSize / Math.Max(_originalWidth, _originalHeight);
            _lastNewW = (int)Math.Round(_originalWidth * scale);
            _lastNewH = (int)Math.Round(_originalHeight * scale);
            
            _offsetX = (_lastModelTargetSize - _lastNewW) / 2;
            _offsetY = (_lastModelTargetSize - _lastNewH) / 2;

            using var resized = original.Resize(new SKImageInfo(_lastNewW, _lastNewH), new SKSamplingOptions(SKFilterMode.Linear));
            
            // 2. Preprocess (RGB, Normalized)
            // Check if it's HWC (Channel Last) or CHW (Channel First)
            bool isChannelLast = (rank == 3 && dims[2] == 3) || (rank == 4 && dims[3] == 3);
            
            System.Diagnostics.Debug.WriteLine($"MobileSAM: Encoder expects input '{inputName}' with rank {rank}, ChannelLast={isChannelLast}");

            DenseTensor<float> inputTensor;
            if (rank == 3)
            {
                inputTensor = isChannelLast 
                    ? new DenseTensor<float>(new[] { modelTargetSize, modelTargetSize, 3 })
                    : new DenseTensor<float>(new[] { 3, modelTargetSize, modelTargetSize });
            }
            else
            {
                inputTensor = isChannelLast 
                    ? new DenseTensor<float>(new[] { 1, modelTargetSize, modelTargetSize, 3 })
                    : new DenseTensor<float>(new[] { 1, 3, modelTargetSize, modelTargetSize });
            }

            for (int y = 0; y < _lastModelTargetSize; y++)
            {
                for (int x = 0; x < _lastModelTargetSize; x++)
                {
                    // Get pixel from resized image, or default (0,0,0,0) if outside resized bounds (padding)
                    // Account for centering offsets
                    int rx = x - _offsetX;
                    int ry = y - _offsetY;
                    
                    var color = (rx >= 0 && rx < _lastNewW && ry >= 0 && ry < _lastNewH) ? resized.GetPixel(rx, ry) : new SKColor(0, 0, 0, 0);
                    
                    // Standard SAM normalization: (x - mean) / std in RGB order
                    // Many models expect RGB [0.485, 0.456, 0.406] and [0.229, 0.224, 0.225]
                    float r = (float)((color.Red / 255.0 - 0.485) / 0.229);
                    float g = (float)((color.Green / 255.0 - 0.456) / 0.224);
                    float b = (float)((color.Blue / 255.0 - 0.406) / 0.225);

                    if (isChannelLast)
                    {
                        if (rank == 3) {
                            inputTensor[y, x, 0] = r; inputTensor[y, x, 1] = g; inputTensor[y, x, 2] = b;
                        } else {
                            inputTensor[0, y, x, 0] = r; inputTensor[0, y, x, 1] = g; inputTensor[0, y, x, 2] = b;
                        }
                    }
                    else
                    {
                        if (rank == 3) {
                            inputTensor[0, y, x] = r; inputTensor[1, y, x] = g; inputTensor[2, y, x] = b;
                        } else {
                            inputTensor[0, 0, y, x] = r; inputTensor[0, 1, y, x] = g; inputTensor[0, 2, y, x] = b;
                        }
                    }
                }
            }

            // 3. Run Encoder to get Embeddings
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var results = _encoderSession.Run(inputs);
            var output = results.First().AsTensor<float>();
            
            // Clone the tensor because the session result is disposed
            _imageEmbeddings = output.ToDenseTensor();
            
            // Log Input info
            var inputStr = string.Join(", ", _encoderSession.InputMetadata.Select(kv => $"{kv.Key}:{string.Join("x", kv.Value.Dimensions)}"));
            System.Diagnostics.Debug.WriteLine($"MobileSAM: Input Meta: {inputStr}");
        });
    }

    public void ResetMaskInput()
    {
        _lowResMask = null;
        System.Diagnostics.Debug.WriteLine("MobileSAM: Mask input reset");
    }

    public async Task<byte[]> GetMaskAsync(IEnumerable<(double X, double Y, bool IsPositive)> points)
    {
        if (_decoderSession == null || _imageEmbeddings == null)
            throw new InvalidOperationException("Service not ready. Call SetImageAsync first.");

        var pointList = points.ToList();
        if (pointList.Count == 0) 
        {
            _lowResMask = null;
            return Array.Empty<byte>();
        }

        return await Task.Run(() =>
        {
            int numPoints = pointList.Count;
            // 1. Prepare Points (Scale physical image coords to SAM TargetSize space)
            // MobileSAM multi-point shape: [1, N, 2]
            var coords = new DenseTensor<float>(new int[] { 1, numPoints, 2 });
            var labels = new DenseTensor<float>(new int[] { 1, numPoints });

            double scale = (double)_lastModelTargetSize / Math.Max(_originalWidth, _originalHeight);
            
            for (int i = 0; i < numPoints; i++)
            {
                var p = pointList[i];
                float px = (float)Math.Clamp(p.X, 0, _originalWidth - 1);
                float py = (float)Math.Clamp(p.Y, 0, _originalHeight - 1);

                // Add _offsetX and _offsetY because image is centered in 1024x1024 space
                coords[0, i, 0] = (float)(px * scale + _offsetX);
                coords[0, i, 1] = (float)(py * scale + _offsetY);
                labels[0, i] = p.IsPositive ? 1f : 0f;
            }

            // 2. Prepare Decoder Inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", _imageEmbeddings),
                NamedOnnxValue.CreateFromTensor("point_coords", coords),
                NamedOnnxValue.CreateFromTensor("point_labels", labels),
                NamedOnnxValue.CreateFromTensor("mask_input", _lowResMask ?? new DenseTensor<float>(new int[] { 1, 1, 256, 256 })),
                NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new float[] { _lowResMask != null ? 1f : 0f }, new int[] { 1 })),
                NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new float[] { (float)_originalHeight, (float)_originalWidth }, new int[] { 2 }))
            };
            
            System.Diagnostics.Debug.WriteLine($"MobileSAM: Running Decoder with {pointList.Count} points");
            using var results = _decoderSession.Run(inputs);
            
            // MobileSAM multi-mask decoder returns:
            // 1. masks: (1, 4, M, M) where M is often 256 or 128
            // 2. iou_predictions: (1, 4)
            var masksResult = results.FirstOrDefault(r => r.Name == "masks" || r.Name == "output" || r.Name == "mask_values") ?? results.First();
            var iouResult = results.FirstOrDefault(r => r.Name == "iou_predictions" || r.Name == "iou");
            
            var masksTensor = masksResult.AsTensor<float>();
            int masksRank = masksTensor.Dimensions.Length;
            int maskSize = masksTensor.Dimensions[masksRank - 1]; // Last dimension is expected to be height/width
            
            // Determine how many masks the model actually returned in the mask tensor
            int availableMasks = masksRank switch {
                4 => masksTensor.Dimensions[1], // (1, C, H, W)
                3 => masksTensor.Dimensions[0], // (C, H, W)
                _ => 1
            };

            int bestMaskIndex = 0;
            string iouInfo = "IOU Missing";
            
            if (iouResult != null)
            {
                try
                {
                    var iouTensor = iouResult.AsTensor<float>();
                    int iouRank = iouTensor.Dimensions.Length;
                    int numMasksReported = iouTensor.Dimensions[iouRank - 1]; // Assume last dim is mask count
                    
                    // Safety: Limit to available masks to prevent out-of-bounds access
                    int safeMaskCount = Math.Min(numMasksReported, availableMasks);
                    
                    float maxIou = -1.0f;
                    var scores = new List<string>();
                    for (int i = 0; i < safeMaskCount; i++)
                    {
                        // Rank-agnostic IOU access with bounds checking
                        float score = 0f;
                        try
                        {
                            score = iouRank switch {
                                2 => iouTensor[0, i],
                                1 => iouTensor[i],
                                _ => iouTensor.First() // Fallback if shape is weird
                            };
                        }
                        catch { score = 0f; }
                        
                        scores.Add(score.ToString("F2"));
                        if (score > maxIou)
                        {
                            maxIou = score;
                            bestMaskIndex = i;
                        }
                    }
                    iouInfo = $"IOU:[{string.Join(",", scores)}] Best:{bestMaskIndex}";
                    System.Diagnostics.Debug.WriteLine($"MobileSAM: {iouInfo}, MasksShape:[{string.Join(",", masksTensor.Dimensions.ToArray())}]");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MobileSAM: IOU parsing error: {ex.Message}");
                    bestMaskIndex = 0; // Fallback to first mask
                }
            }
            
            // CRITICAL: Clamp bestMaskIndex to actual available masks in the main tensor to prevent IndexOutOfRangeException
            bestMaskIndex = Math.Clamp(bestMaskIndex, 0, availableMasks - 1);
            _lastIouInfo = iouInfo;

            // 3. Store the low-res mask for the next iteration (iterative refinement)
            // MobileSAM often returns a second output or has low-res masks in the bundle
            // If the model output has a "low_res_masks" or similar, we should use it.
            // In many SAM versions, it's (1, 4, 64, 64) or similar.
            var lowResMaskResult = results.FirstOrDefault(r => r.Name == "low_res_masks");
            if (lowResMaskResult != null)
            {
                var lrmTensor = lowResMaskResult.AsTensor<float>();
                int lrmRank = lrmTensor.Dimensions.Length;
                int lrmMaskCount = lrmRank switch { 4 => lrmTensor.Dimensions[1], 3 => lrmTensor.Dimensions[0], _ => 1 };
                int safeLrmIdx = Math.Clamp(bestMaskIndex, 0, lrmMaskCount - 1);
                
                // Extract only the best low-res mask
                int lrmH = lrmTensor.Dimensions[lrmRank - 2];
                int lrmW = lrmTensor.Dimensions[lrmRank - 1];
                var bestLrm = new DenseTensor<float>(new int[] { 1, 1, lrmH, lrmW });
                for(int ly=0; ly<lrmH; ly++)
                    for(int lx=0; lx<lrmW; lx++)
                        bestLrm[0, 0, ly, lx] = lrmRank == 4 ? lrmTensor[0, safeLrmIdx, ly, lx] : lrmTensor[safeLrmIdx, ly, lx];
                
                _lowResMask = bestLrm;
            }
            else
            {
                 // If no explicit low-res mask output, reset it to prevent stale data
                 _lowResMask = null;
            }

            // 3. Reconstruct Mask using Skia for high precision
            // The mask tensor represents the FULL 1024x1024 padded canvas, not just the image
            // We need to extract the region that corresponds to the actual image content
            int actualMaskW = masksRank >= 2 ? masksTensor.Dimensions[masksRank - 1] : maskSize;
            int actualMaskH = masksRank >= 2 ? masksTensor.Dimensions[masksRank - 2] : maskSize;
            
            using var maskFull = new SKBitmap(actualMaskW, actualMaskH, SKColorType.Gray8, SKAlphaType.Opaque);
            
            System.Diagnostics.Debug.WriteLine($"MobileSAM: TensorDims=[{string.Join(",", masksTensor.Dimensions.ToArray())}] MaskSize=({actualMaskW}x{actualMaskH}) bestMaskIdx={bestMaskIndex}");
            
            for (int y = 0; y < actualMaskH; y++) {
                for (int x = 0; x < actualMaskW; x++) {
                    try
                    {
                        float logit = masksRank switch
                        {
                            4 => masksTensor[0, bestMaskIndex, y, x],
                            3 => masksTensor[bestMaskIndex, y, x],
                            _ => masksTensor.First()
                        };
                        maskFull.SetPixel(x, y, logit > 0 ? new SKColor(255, 255, 255) : new SKColor(0, 0, 0));
                    }
                    catch { maskFull.SetPixel(x, y, new SKColor(0, 0, 0)); }
                }
            }
            
            // Calculate the active region in mask space (where the actual image content is)
            double maskScale = (double)_lastModelTargetSize / Math.Max(_originalWidth, _originalHeight);
            
            // Scaled content size in the current mask resolution
            int activeW = (int)Math.Round(_originalWidth * scale * actualMaskW / _lastModelTargetSize);
            int activeH = (int)Math.Round(_originalHeight * scale * actualMaskH / _lastModelTargetSize);
            
            // Offsets in the current mask resolution
            int mOffsetX = (int)Math.Round((double)_offsetX * actualMaskW / _lastModelTargetSize);
            int mOffsetY = (int)Math.Round((double)_offsetY * actualMaskH / _lastModelTargetSize);
            
            activeW = Math.Clamp(activeW, 1, actualMaskW - mOffsetX);
            activeH = Math.Clamp(activeH, 1, actualMaskH - mOffsetY);
            
            System.Diagnostics.Debug.WriteLine($"MobileSAM MASK: OrigSize=({_originalWidth}x{_originalHeight}) Scale={maskScale:F4} ActiveRegion=({activeW}x{activeH}) Offset=({mOffsetX},{mOffsetY}) in mask space");
            
            // Extract the active region (the part that represents the actual image, accounting for center padding)
            using var activeMask = new SKBitmap(activeW, activeH, SKColorType.Gray8, SKAlphaType.Opaque);
            maskFull.ExtractSubset(activeMask, new SKRectI(mOffsetX, mOffsetY, mOffsetX + activeW, mOffsetY + activeH));
            
            // Resize the active region to match original image dimensions using SkiaSharp's Resize
            using var resizedMask = activeMask.Resize(new SKImageInfo(_originalWidth, _originalHeight), new SKSamplingOptions(SKFilterMode.Nearest));
            
            // Create the final visual representation
            using var visualBmp = new SKBitmap(_originalWidth, _originalHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            
            for (int py = 0; py < _originalHeight; py++) {
                for (int px = 0; px < _originalWidth; px++) {
                    if (resizedMask.GetPixel(px, py).Red > 128) {
                        visualBmp.SetPixel(px, py, new SKColor(255, 200, 0, 180)); // Yellowish selection
                    } else {
                        visualBmp.SetPixel(px, py, SKColors.Transparent);
                    }
                }
            }

            using var visualImage = SKImage.FromBitmap(visualBmp);
            using var data = visualImage.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        });
    }

    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
    }
}
