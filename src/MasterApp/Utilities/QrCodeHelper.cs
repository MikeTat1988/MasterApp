using QRCoder;

namespace MasterApp.Utilities;

public static class QrCodeHelper
{
    public static string GenerateSvg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(12);
    }
}
