using System;
using System.Management;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class CapabilityDetectionService
{
    public BoardConfiguration DetectBoard()
    {
        string boardId = "UNKNOWN";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
            foreach (var obj in searcher.Get())
            {
                boardId = obj["Product"]?.ToString() ?? "UNKNOWN";
                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CapabilityDetection] Failed to read Win32_BaseBoard: {ex.Message}");
        }

        Console.WriteLine($"[CapabilityDetection] Detected Board ID: {boardId}");

        // Build config based on BoardId (Safety first: default to safe fallback)
        return ModelCapabilityDatabase.GetCapabilities(boardId);
    }
}
