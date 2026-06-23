using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class PresetService : IPresetService
{
    private static readonly string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmenFlow");
    private static readonly string PresetsFilePath = Path.Combine(AppDataPath, "presets.json");

    private readonly List<FanPreset> _builtInPresets = new()
    {
        new FanPreset("built_in_silent", "Silent", true, false, new FanCurve(FanTarget.Both, new List<FanCurvePoint>
        {
            new FanCurvePoint(30, 20),
            new FanCurvePoint(50, 30),
            new FanCurvePoint(70, 45),
            new FanCurvePoint(85, 55),
            new FanCurvePoint(95, 65)
        })),
        new FanPreset("built_in_balanced", "Balanced", true, false, new FanCurve(FanTarget.Both, new List<FanCurvePoint>
        {
            new FanCurvePoint(30, 25),
            new FanCurvePoint(50, 40),
            new FanCurvePoint(70, 60),
            new FanCurvePoint(85, 75),
            new FanCurvePoint(95, 90)
        })),
        new FanPreset("built_in_gaming", "Gaming", true, false, new FanCurve(FanTarget.Both, new List<FanCurvePoint>
        {
            new FanCurvePoint(30, 35),
            new FanCurvePoint(50, 55),
            new FanCurvePoint(70, 75),
            new FanCurvePoint(85, 90),
            new FanCurvePoint(95, 100)
        })),
        new FanPreset("built_in_max", "Max", true, true, null)
    };

    private List<FanPreset> _userPresets = new();

    public PresetService()
    {
        LoadUserPresets();
    }

    public IReadOnlyList<FanPreset> GetAll()
    {
        return _builtInPresets.Concat(_userPresets).ToList();
    }

    public void Save(FanPreset preset)
    {
        if (preset.IsBuiltIn)
        {
            throw new InvalidOperationException("Cannot save or modify a built-in preset.");
        }

        var existing = _userPresets.FirstOrDefault(p => p.Id == preset.Id);
        if (existing != null)
        {
            _userPresets.Remove(existing);
        }

        _userPresets.Add(preset);
        SaveUserPresets();
    }

    public void Delete(string id)
    {
        var existing = GetAll().FirstOrDefault(p => p.Id == id);
        if (existing != null && existing.IsBuiltIn)
        {
            throw new InvalidOperationException("Cannot delete a built-in preset.");
        }

        var userPreset = _userPresets.FirstOrDefault(p => p.Id == id);
        if (userPreset != null)
        {
            _userPresets.Remove(userPreset);
            SaveUserPresets();
        }
    }

    private void LoadUserPresets()
    {
        try
        {
            if (File.Exists(PresetsFilePath))
            {
                var json = File.ReadAllText(PresetsFilePath);
                var loaded = JsonSerializer.Deserialize<List<FanPreset>>(json);
                if (loaded != null)
                {
                    _userPresets = loaded.Where(p => !p.IsBuiltIn).ToList();
                }
            }
        }
        catch
        {
            // Fallback to empty if file is corrupt
            _userPresets = new List<FanPreset>();
        }
    }

    private void SaveUserPresets()
    {
        try
        {
            if (!Directory.Exists(AppDataPath))
            {
                Directory.CreateDirectory(AppDataPath);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_userPresets, options);
            File.WriteAllText(PresetsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save user presets: {ex}");
        }
    }
}
