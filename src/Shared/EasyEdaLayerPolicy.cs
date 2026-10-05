using System;
using System.Collections.Generic;

namespace LcscBridge.Shared
{
    public static class EasyEdaLayerPolicy
    {
        private static readonly HashSet<string> SupportedLayers = new HashSet<string>(StringComparer.Ordinal)
        {
            "TopLayer", "BottomLayer", "TopSilkLayer", "BottomSilkLayer",
            "TopPasteMaskLayer", "BottomPasteMaskLayer", "TopSolderMaskLayer", "BottomSolderMaskLayer",
            "BoardOutline", "BoardOutLine", "Multi-Layer", "TopAssembly", "BottomAssembly", "Mechanical",
            "3DModel", "Document", "ComponentShapeLayer", "LeadShapeLayer",
            "ComponentMarkingLayer", "ComponentPolarityLayer"
        };

        private static readonly Dictionary<string, int> LayerFieldByPrimitive = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "PAD", 6 }, { "TRACK", 2 }, { "CIRCLE", 5 }, { "ARC", 2 },
            { "RECT", 5 }, { "TEXT", 7 }, { "SOLIDREGION", 1 }
        };

        public static bool IsSupportedLayer(string layerName)
        {
            return !string.IsNullOrWhiteSpace(layerName) && SupportedLayers.Contains(layerName);
        }

        public static bool TryGetLayerFieldIndex(string primitive, out int index)
        {
            return LayerFieldByPrimitive.TryGetValue(primitive ?? string.Empty, out index);
        }
    }
}
