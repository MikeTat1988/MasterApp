using QRCoder;
using System.Globalization;
using System.Security;

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

    public static string GenerateMessageSvg(string title, string body)
    {
        var safeTitle = SecurityElement.Escape(title) ?? string.Empty;
        var safeBody = SecurityElement.Escape(body) ?? string.Empty;
        const string background = "#f6f8fb";
        const string border = "#d8e0ea";
        const string titleColor = "#111723";
        const string bodyColor = "#5d6b80";

        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="360" height="360" viewBox="0 0 360 360" role="img" aria-label="{{safeTitle}}">
              <rect width="360" height="360" rx="28" fill="{{background}}" />
              <rect x="18" y="18" width="324" height="324" rx="22" fill="#ffffff" stroke="{{border}}" stroke-width="2" />
              <text x="180" y="142" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="26" font-weight="700" fill="{{titleColor}}">{{safeTitle}}</text>
              <text x="180" y="188" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="18" fill="{{bodyColor}}">{{safeBody}}</text>
            </svg>
            """);
    }
}
