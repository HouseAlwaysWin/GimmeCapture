namespace GimmeCapture.Models;

public class AnnotationEffectSettings
{
    public int MosaicCellSize { get; set; } = 12;
    public float BlurRadius { get; set; } = 16f;

    public AnnotationEffectSettings Clone()
    {
        return new AnnotationEffectSettings
        {
            MosaicCellSize = MosaicCellSize,
            BlurRadius = BlurRadius
        };
    }
}
