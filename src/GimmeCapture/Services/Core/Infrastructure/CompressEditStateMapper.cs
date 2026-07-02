using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>One persisted annotation of a compress item (JSON-friendly mirror of <see cref="Annotation"/>).</summary>
public sealed class CompressAnnotationState
{
    public string Type { get; set; } = nameof(AnnotationType.Rectangle);
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }

    /// <summary>Pen stroke points as [x0, y0, x1, y1, …].</summary>
    public List<double>? Points { get; set; }

    public string ColorHex { get; set; } = "#FFFF0000";
    public double Thickness { get; set; }
    public double FontSize { get; set; }
    public string FontFamily { get; set; } = "Arial";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsFilled { get; set; }
    public string Text { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public int MosaicCellSize { get; set; } = 12;
    public float BlurRadius { get; set; } = 16f;
    public float Feather { get; set; }
}

public sealed class CompressRedactionKeyframeState
{
    public double Time { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class CompressRedactionTrackState
{
    public string Effect { get; set; } = nameof(RedactionEffect.Blur);
    public List<CompressRedactionKeyframeState> Keyframes { get; set; } = new();
}

/// <summary>
/// Maps the 進階影片編輯 burn-in layers (annotations / redaction tracks) to/from the JSON DTOs persisted in
/// <see cref="CompressItemState"/>, so a restarted app restores fully re-editable edits. The Mosaic/Blur
/// <c>DrawingModeSnapshot</c> (a preview-only bitmap) is intentionally not persisted — the editor re-captures
/// it from the decoded frame on load, and the encode-time burn-in samples the real frame anyway.
/// </summary>
internal static class CompressEditStateMapper
{
    public static List<CompressAnnotationState> ToState(IEnumerable<Annotation> annotations) =>
        annotations.Select(a => new CompressAnnotationState
        {
            Type = a.Type.ToString(),
            StartX = a.StartPoint.X,
            StartY = a.StartPoint.Y,
            EndX = a.EndPoint.X,
            EndY = a.EndPoint.Y,
            Points = a.Points.Count > 0
                ? a.Points.SelectMany(p => new[] { p.X, p.Y }).ToList()
                : null,
            ColorHex = a.Color.ToString(),
            Thickness = a.Thickness,
            FontSize = a.FontSize,
            FontFamily = a.FontFamily?.Name ?? "Arial",
            IsBold = a.IsBold,
            IsItalic = a.IsItalic,
            IsFilled = a.IsFilled,
            Text = a.Text,
            StepNumber = a.StepNumber,
            MosaicCellSize = a.EffectSettings.MosaicCellSize,
            BlurRadius = a.EffectSettings.BlurRadius,
            Feather = a.EffectSettings.Feather,
        }).ToList();

    public static List<Annotation> FromState(IEnumerable<CompressAnnotationState> states)
    {
        var result = new List<Annotation>();
        foreach (CompressAnnotationState s in states)
        {
            if (!Enum.TryParse(s.Type, out AnnotationType type) || type == AnnotationType.None)
            {
                continue; // unknown/legacy type — skip rather than fail the whole item
            }

            Color color;
            try
            {
                color = Color.Parse(s.ColorHex);
            }
            catch (FormatException)
            {
                color = Colors.Red;
            }

            var ann = new Annotation
            {
                Type = type,
                StartPoint = new Point(s.StartX, s.StartY),
                EndPoint = new Point(s.EndX, s.EndY),
                Color = color,
                Thickness = s.Thickness,
                FontSize = s.FontSize,
                FontFamily = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? "Arial" : s.FontFamily),
                IsBold = s.IsBold,
                IsItalic = s.IsItalic,
                IsFilled = s.IsFilled,
                Text = s.Text,
                StepNumber = s.StepNumber,
                EffectSettings = new AnnotationEffectSettings
                {
                    MosaicCellSize = s.MosaicCellSize,
                    BlurRadius = s.BlurRadius,
                    Feather = s.Feather,
                },
            };

            if (s.Points is { Count: >= 2 })
            {
                for (int i = 0; i + 1 < s.Points.Count; i += 2)
                {
                    ann.Points.Add(new Point(s.Points[i], s.Points[i + 1]));
                }
            }

            result.Add(ann);
        }

        return result;
    }

    public static List<CompressRedactionTrackState> ToState(IEnumerable<RedactionTrack> tracks) =>
        tracks.Where(t => t.Keyframes.Count > 0).Select(t => new CompressRedactionTrackState
        {
            Effect = t.Effect.ToString(),
            Keyframes = t.Keyframes.Select(k => new CompressRedactionKeyframeState
            {
                Time = k.TimeSeconds,
                X = k.X,
                Y = k.Y,
                Width = k.Width,
                Height = k.Height,
            }).ToList(),
        }).ToList();

    public static List<RedactionTrack> FromState(IEnumerable<CompressRedactionTrackState> states)
    {
        var result = new List<RedactionTrack>();
        foreach (CompressRedactionTrackState s in states)
        {
            var track = new RedactionTrack
            {
                Effect = Enum.TryParse(s.Effect, out RedactionEffect effect) ? effect : RedactionEffect.Blur,
            };
            track.Keyframes.AddRange(s.Keyframes.Select(k => new RedactionKeyframe
            {
                TimeSeconds = k.Time,
                X = k.X,
                Y = k.Y,
                Width = k.Width,
                Height = k.Height,
            }));
            result.Add(track);
        }

        return result;
    }
}
