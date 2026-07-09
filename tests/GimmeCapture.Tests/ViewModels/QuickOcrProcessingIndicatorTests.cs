using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;
using SkiaSharp;

namespace GimmeCapture.Tests.ViewModels;

// Guards the Quick-OCR "not frozen" fix: while OCR recognition runs, the snip overlay (which hosts the in-window
// ShowProcessingOverlay) is hidden to grab a clean capture, so a standalone spinner window must be shown instead —
// otherwise the multi-second recognition looks like the app has frozen. The spinner must be shown only AFTER the
// capture (so it is not grabbed into the OCR image) and must ALWAYS be torn down, including on failure/cancel.
public sealed class QuickOcrProcessingIndicatorTests
{
    private static Mock<IScreenCaptureService> CreateCaptureMock()
    {
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new SKBitmap(1, 1));
        capture
            .Setup(c => c.CopyToClipboardAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        return capture;
    }

    private static async Task<SnipWindowViewModel> CreateViewModelAsync(IScreenCaptureService capture, IQuickOcrService ocr)
    {
        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;

        var vm = new SnipWindowViewModel(
            Colors.Red,
            2.0,
            captureService: capture,
            detectionService: null,
            recService: null,
            mainVm: mainVm,
            translationSession: null,
            translationSelectionMonitor: null,
            aiScanSessionService: null,
            quickOcrService: ocr,
            captureVisibilityCoordinator: new ImmediateCaptureVisibilityCoordinator());
        vm.SelectionRect = new Rect(0, 0, 100, 100);
        return vm;
    }

    [Fact]
    public async Task QuickOcr_ShowsSpinnerAfterCaptureAndHidesItAfterRecognition()
    {
        var capture = CreateCaptureMock();

        int order = 0;
        int captureOrder = 0, showOrder = 0, recognizeOrder = 0, hideOrder = 0;

        capture
            .Setup(c => c.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(), It.IsAny<bool>()))
            .ReturnsAsync(() => { captureOrder = ++order; return new SKBitmap(1, 1); });

        var ocr = new Mock<IQuickOcrService>();
        ocr
            .Setup(o => o.RecognizeAsync(It.IsAny<SKBitmap>(), It.IsAny<OCRLanguage>(), It.IsAny<OcrTextLayout>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { recognizeOrder = ++order; return new QuickOcrResult(QuickOcrStatus.Success, "hello"); });

        using var vm = await CreateViewModelAsync(capture.Object, ocr.Object);
        vm.ShowProcessingWindowAction = () => showOrder = ++order;
        vm.HideProcessingWindowAction = () => hideOrder = ++order;

        await vm.RunQuickOcrTextCopyForTestAsync();

        Assert.True(showOrder > 0, "The processing spinner must be shown.");
        Assert.True(hideOrder > 0, "The processing spinner must be hidden.");
        Assert.True(captureOrder < showOrder, "Spinner must appear only AFTER the capture, so it isn't grabbed into the OCR image.");
        Assert.True(showOrder < recognizeOrder, "Spinner must be shown BEFORE recognition starts.");
        Assert.True(hideOrder > recognizeOrder, "Spinner must be hidden AFTER recognition finishes.");
    }

    [Theory]
    [InlineData(QuickOcrStatus.Failed)]
    [InlineData(QuickOcrStatus.NoText)]
    [InlineData(QuickOcrStatus.ModuleMissing)]
    public async Task QuickOcr_HidesSpinnerEvenWhenRecognitionDoesNotSucceed(QuickOcrStatus status)
    {
        var capture = CreateCaptureMock();

        var ocr = new Mock<IQuickOcrService>();
        ocr
            .Setup(o => o.RecognizeAsync(It.IsAny<SKBitmap>(), It.IsAny<OCRLanguage>(), It.IsAny<OcrTextLayout>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuickOcrResult(status));

        using var vm = await CreateViewModelAsync(capture.Object, ocr.Object);
        bool shown = false, hidden = false;
        vm.ShowProcessingWindowAction = () => shown = true;
        vm.HideProcessingWindowAction = () => hidden = true;

        await vm.RunQuickOcrTextCopyForTestAsync();

        Assert.True(shown, "The processing spinner must be shown before recognition.");
        Assert.True(hidden, "The processing spinner must always be hidden once recognition returns, even on a non-success result.");
    }

    [Fact]
    public async Task QuickOcr_HidesSpinnerWhenRecognitionThrows()
    {
        var capture = CreateCaptureMock();

        var ocr = new Mock<IQuickOcrService>();
        ocr
            .Setup(o => o.RecognizeAsync(It.IsAny<SKBitmap>(), It.IsAny<OCRLanguage>(), It.IsAny<OcrTextLayout>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.InvalidOperationException("ocr boom"));

        using var vm = await CreateViewModelAsync(capture.Object, ocr.Object);
        bool hidden = false;
        vm.ShowProcessingWindowAction = () => { };
        vm.HideProcessingWindowAction = () => hidden = true;

        // An unexpected recognition error propagates out (only cancellation is caught), but the finally must still
        // tear the spinner down first — otherwise a stuck spinner would outlive the failed OCR.
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => vm.RunQuickOcrTextCopyForTestAsync());

        Assert.True(hidden, "The processing spinner must be hidden in the finally block even when recognition throws.");
    }
}
