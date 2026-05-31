using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace HoneyOverlays.Services;

/// <summary>
/// Parser für die Asset-Liste, die fetch.tradingpaints.gg zurückgibt.
/// Portiert aus Alphasumsi/MarvinsAIRARefactored (Classes/TradingPaintsXML.cs).
/// </summary>
public static class TradingPaintsXml
{
    public enum AssetType
    {
        Unknown = 0,
        Car,
        CarNum,
        CarSpec,
        CarDecal,
        Suit,
        Helmet
    }

    public sealed class Asset
    {
        public string FileId { get; init; } = string.Empty;
        public string FileURL { get; init; } = string.Empty;
        public long UserID { get; init; }
        public string Directory { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public AssetType Type { get; init; }
        public int TeamId { get; init; }
        public string? Ext { get; init; }
    }

    public static IReadOnlyList<Asset> ParseAssets(Stream xmlStream)
    {
        ArgumentNullException.ThrowIfNull(xmlStream);

        var doc = XDocument.Load(xmlStream);
        var carsElement = doc.Root?.Element("Cars");
        if (carsElement is null) return Array.Empty<Asset>();

        var results = new List<Asset>();
        foreach (var carElement in carsElement.Elements("Car"))
        {
            var directory = (string?)carElement.Element("directory") ?? string.Empty;
            var fileUrl = (string?)carElement.Element("file") ?? string.Empty;
            var userId = ParseInt64((string?)carElement.Element("userid"));
            var fileSize = ParseInt64((string?)carElement.Element("filesize"));
            var teamId = ParseInt32((string?)carElement.Element("teamid"));

            results.Add(new Asset
            {
                FileId = (string?)carElement.Element("carid") ?? string.Empty,
                FileURL = fileUrl,
                UserID = userId,
                Directory = (directory == "suits" || directory == "helmets") ? string.Empty : directory,
                FileSize = fileSize,
                Type = ParseType((string?)carElement.Element("type")),
                TeamId = teamId,
                Ext = (string?)carElement.Element("ext")
            });
        }
        return results;
    }

    private static AssetType ParseType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "car" => AssetType.Car,
            "car_num" => AssetType.CarNum,
            "car_spec" => AssetType.CarSpec,
            "car_decal" => AssetType.CarDecal,
            "suit" => AssetType.Suit,
            "helmet" => AssetType.Helmet,
            _ => AssetType.Unknown
        };

    private static long ParseInt64(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;

    private static int ParseInt32(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
