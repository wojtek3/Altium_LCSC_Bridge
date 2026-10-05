// Added by Altium LCSC Bridge under GPL-3.0.
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyEDA_Loader
{
    public sealed class EeFootprintSolidRegion : EeFootprintShape
    {
        public string LayerId { get; private set; }
        public string Id { get; private set; }
        public List<EePoint> Points { get; private set; }

        public static EeFootprintSolidRegion FromString(string data)
        {
            var parts = data.Split(new[] { "~" }, StringSplitOptions.None);
            if (parts.Length < 6) throw new FormatException("Malformed EasyEDA solid region.");
            var path = parts[3];
            var commands = Regex.Matches(path, "[A-Za-z]").Cast<Match>().Select(x => x.Value).ToList();
            if (commands.Any(x => x != "M" && x != "L" && x != "Z"))
                throw new NotSupportedException("Only straight, absolute EasyEDA solid regions are supported.");
            var numbers = Regex.Matches(path, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
                .Cast<Match>().Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
            if (numbers.Count < 6 || numbers.Count % 2 != 0) throw new FormatException("The EasyEDA solid region has an invalid point list.");
            var points = new List<EePoint>();
            for (var i = 0; i < numbers.Count; i += 2)
                points.Add(new EePoint { X = ConvertToMM(numbers[i]), Y = ConvertToMM(numbers[i + 1]) });
            return new EeFootprintSolidRegion { LayerId = parts[1], Id = parts[5], Points = points };
        }

        public override List<UIElement> AddToCanvas(Canvas canvas, EeFootprintContext context)
        {
            var polygon = new Polygon
            {
                Fill = new SolidColorBrush(ColorHelper.FromHex(context.Layers.GetLayerColor(LayerId))),
                Opacity = 0.65
            };
            foreach (var point in Points)
                polygon.Points.Add(new Point(point.X - context.Box.X, point.Y - context.Box.Y));
            return new List<UIElement> { polygon };
        }

        public override bool AddToComponent(IPCB_LibComponent component, EeFootprintContext context)
        {
            try
            {
                var sourceLayer = context.Layers.GetLayer(LayerId) ?? throw new EEPCB.LayerMapException("Missing EasyEDA layer " + LayerId);
                var targetLayer = EEPCB.EELayerToAltium(sourceLayer.Name);
                var region = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eRegionObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Region
                    ?? throw new InvalidOperationException("Altium could not create a PCB region.");
                var contour = AltiumApi.GlobalVars.PCBServer.PCBContourFactory();
                foreach (var point in Points)
                    contour.AddPoint(AltiumApi.MmToCoord(ConvertX(point.X, context)) + component.GetState_XLocation(),
                        AltiumApi.MmToCoord(ConvertY(point.Y, context)) + component.GetState_YLocation());
                region.SetOutlineContour(contour);
                region.SetState_Kind(TRegionKind.eRegionKind_Copper);
                region.SetState_V7Layer(new V7_Layer(targetLayer));
                EEPCB.AddToPCB(component, region);
                return true;
            }
            catch (Exception ex)
            {
                return context.Exception == null || context.Exception(ex);
            }
        }
    }
}
