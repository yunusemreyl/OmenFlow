using System.Linq;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public static class ModelCapabilityDatabase
{
    private static readonly string[] OmenLegacyBoards = {
        "8607", "8746", "8747", "8748", "8749", "874A"
    };

    private static readonly string[] VictusBoards = {
        "88F8", "8A25"
    };

    private static readonly string[] VictusSBoards = {
        "8902", "8A44", "8A4D", "8BAB", "8BBE", "8BC2", "8BCA", "8BCD", "8BD4", "8BD5", "8C76", "8C77", "8C78", "8C99", "8C9C", "8D41", "8D87"
    };

    private static readonly string[] OmenV1Boards = {
        "84DA", "84DB", "84DC", "8572", "8573", "8574", "8575", "8600", "8601", "8602", "8603", "8604", "8605", "8606", "860A",
        "8786", "8787", "8788", "878A", "878B", "878C", "87B5", "886B", "886C", "88C8", "88CB", "88D1", "88D2", "88F4", "88F5", "88F6",
        "88F7", "88FD", "88FE", "88FF", "8900", "8901", "8912", "8917", "8918", "8949", "894A", "89EB", "8A15", "8A42", "8BAD", "8C58", "8E41"
    };

    public static BoardConfiguration GetCapabilities(string boardId)
    {
        DeviceFamily family = DeviceFamily.Unknown;

        if (VictusSBoards.Contains(boardId))
        {
            family = DeviceFamily.VictusS;
        }
        else if (VictusBoards.Contains(boardId))
        {
            family = DeviceFamily.Victus;
        }
        else if (OmenLegacyBoards.Contains(boardId))
        {
            family = DeviceFamily.OmenLegacy;
        }
        else if (OmenV1Boards.Contains(boardId))
        {
            family = DeviceFamily.OmenV1;
        }

        // Fan scale rules: some V2 fan models use 0-100%, others 0-55. 
        // 8BAB and 8D41 are known to be 0-100% models based on earlier tests.
        int maxFanLevel = (boardId is "8BAB" or "8D41") ? 100 : 55;

        // Legacy / Standard 2020-2022 Omen Models had EC offset
        bool hasEcThermalOffset = family == DeviceFamily.OmenV1 && (boardId is "8A14" or "8BAD" or "8BAF" or "8BB0" or "8CD0" or "8CD1");

        return new BoardConfiguration(
            BoardId: boardId,
            Family: family,
            HasEcThermalOffset: hasEcThermalOffset,
            MaxFanLevel: maxFanLevel,
            SupportsDetailedPowerLimits: false, // Default to false for everyone to force WMI logic instead of raw EC
            UseSimplifiedPerformanceMode: true  // Default true to allow redundant safe EC fallback (0xCE) if needed
        );
    }
}
