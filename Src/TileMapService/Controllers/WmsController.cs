﻿using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using TileMapService.Utils;
using TileMapService.Wms;

namespace TileMapService.Controllers
{
    /// <summary>
    /// Serving tiles using Web Map Service (WMS) protocol.
    /// </summary>
    /// <remarks>
    /// Supports currently only EPSG:3857; WMS versions 1.1.1 and 1.3.0
    /// </remarks>
    [Route("wms")]
    public class WmsController : Controller
    {
        private readonly ITileSourceFabric tileSourceFabric;

        public WmsController(ITileSourceFabric tileSourceFabric)
        {
            this.tileSourceFabric = tileSourceFabric;
        }

        [HttpGet]
        [Route("")]
        public async Task<IActionResult> ProcessWmsRequestAsync(
              string service = Identifiers.Wms,
              string version = Identifiers.Version111,
              string request = null,
              string layers = null,
              ////string styles = null,
              string srs = null,
              string crs = null, // WMS version 1.3.0
              string bbox = null,
              int width = 0,
              int height = 0,
              string format = null,
              // Optional GetMap request parameters
              bool? transparent = false,
              string bgcolor = Identifiers.DefaultBackgroundColor,
              string exceptions = MediaTypeNames.Application.OgcServiceExceptionXml,
              ////string time = null,
              ////string sld = null,
              ////string sld_body = null,
              // GetFeatureInfo request parameters
              string query_layers = null,
              string info_format = MediaTypeNames.Text.Plain,
              int x = 0,
              int y = 0,
              int i = 0, // WMS version 1.3.0
              int j = 0, // WMS version 1.3.0
              int feature_count = 1)
        {
            // $"WMS [{Request.GetOwinContext().Request.RemoteIpAddress}:{Request.GetOwinContext().Request.RemotePort}] {Request.RequestUri}";

            // TODO: errors in XML format

            if (String.Compare(service, Identifiers.Wms, StringComparison.Ordinal) != 0)
            {
                var s = $"Unsupported {nameof(service)} type: {service} (should be {Identifiers.Wms})";
                return BadRequest(s);
            }

            if ((String.Compare(version, Identifiers.Version111, StringComparison.Ordinal) != 0) &&
                (String.Compare(version, Identifiers.Version130, StringComparison.Ordinal) != 0))
            {
                var s = $"Unsupported {nameof(version)}: {version} (should be one of: {Identifiers.Version111}, {Identifiers.Version130})";
                return BadRequest(s);
            }

            if ((String.Compare(exceptions, MediaTypeNames.Application.OgcServiceExceptionXml, StringComparison.Ordinal) != 0) &&
                (String.Compare(exceptions, "XML", StringComparison.Ordinal) != 0))
            {
                var s = $"Unsupported {nameof(exceptions)}: {exceptions} (should be {MediaTypeNames.Application.OgcServiceExceptionXml})";
                return BadRequest(s);
            }

            var wmsVersion = WmsHelper.GetWmsVersion(version);
            if (String.Compare(request, Identifiers.GetCapabilities, StringComparison.Ordinal) == 0)
            {
                return this.ProcessGetCapabilitiesRequest(wmsVersion);
            }
            else if (String.Compare(request, Identifiers.GetMap, StringComparison.Ordinal) == 0)
            {
                return await this.ProcessGetMapRequestAsync(
                    layers,
                    wmsVersion == Wms.Version.Version130 ? crs : srs,
                    bbox, width, height,
                    format, transparent, bgcolor);
            }
            else if (String.Compare(request, Identifiers.GetFeatureInfo, StringComparison.Ordinal) == 0)
            {
                return BadRequest();
                ////return await this.ProcessGetFeatureInfoRequestAsync(
                ////    bbox, width,
                ////    query_layers, info_format,
                ////    v == Wms.Version.v130 ? i : x,
                ////    v == Wms.Version.v130 ? j : y,
                ////    feature_count);
            }
            else
            {
                var s = $"Unsupported request: {request} ({Identifiers.GetCapabilities}, {Identifiers.GetMap}, {Identifiers.GetFeatureInfo})";
                return BadRequest(s);
            }
        }

        private IActionResult ProcessGetCapabilitiesRequest(Wms.Version version)
        {
            var layers = EntitiesConverter.SourcesToLayers(this.tileSourceFabric.Sources)
                .Where(l => l.Srs == SrsCodes.EPSG3857) // TODO: EPSG:4326 support
                .Select(l => new Layer
                {
                    Name = l.Identifier,
                    Title = String.IsNullOrEmpty(l.Title) ? l.Identifier : l.Title,
                    Abstract = String.Empty, // TODO: Fill Abstract field
                    IsQueryable = false,
                })
                .ToList();

            var xmlDoc = new CapabilitiesBuilder(BaseUrl + "/wms").GetCapabilities(
                version,
                new Service
                {
                    Title = "WMS",
                    Abstract = "WMS",
                },
                layers,
                new[]
                {
                    MediaTypeNames.Image.Png,
                    MediaTypeNames.Image.Jpeg,
                },
                new[]
                {
                    MediaTypeNames.Text.Plain,
                });

            return File(xmlDoc.ToUTF8ByteArray(), MediaTypeNames.Text.Xml);
        }

        private async Task<IActionResult> ProcessGetMapRequestAsync(
            string layers,
            string srs, 
            string bbox,
            int width, 
            int height,
            string format, 
            bool? transparent, 
            string bgcolor)
        {
            // TODO: config ?
            const int MinSize = 32;
            const int MaxSize = 32768;

            if (String.IsNullOrEmpty(layers))
            {
                return BadRequest("layers");
            }

            if ((width < MinSize) || (width > MaxSize))
            {
                return BadRequest("width");
            }

            if ((height < MinSize) || (height > MaxSize))
            {
                return BadRequest("height");
            }

            var isFormatSupported = (String.Compare(format, MediaTypeNames.Image.Png, StringComparison.OrdinalIgnoreCase) == 0) ||
                                    (String.Compare(format, MediaTypeNames.Image.Jpeg, StringComparison.OrdinalIgnoreCase) == 0);

            if (!isFormatSupported)
            {
                return BadRequest("format");
            }

            if (String.Compare(srs, Identifiers.EPSG3857, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return BadRequest("srs"); // TODO: EPSG4326
            }

            var boundingBox = Models.Bounds.FromMBTilesMetadataString(bbox);
            if (boundingBox == null)
            {
                return BadRequest("bbox");
            }

            if (boundingBox.Right < boundingBox.Left)
            {
                return BadRequest("bbox");
            }

            var layersList = layers.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var isTransparent = transparent.HasValue ? transparent.Value : false;
            var backgroundColor = EntitiesConverter.GetArgbColorFromString(bgcolor, isTransparent);
            var imageData = await CreateMapImageAsync(width, height, boundingBox, format, backgroundColor, layersList);

            return File(imageData, format);
        }

        private async Task<byte[]> CreateMapImageAsync(
            int width, int height,
            Models.Bounds boundingBox,
            string format,
            int backgroundColor,
            IList<string> layerNames)
        {
            var emptyImage = ImageHelper.CreateEmptyPngImage(width, height, backgroundColor);
            using (var resultImageStream = new MemoryStream(emptyImage))
            {
                using var resultImage = new Bitmap(resultImageStream);

                foreach (var layerName in layerNames)
                {
                    if (this.tileSourceFabric.Contains(layerName))
                    {
                        await DrawLayerAsync(
                            width, boundingBox,
                            resultImage,
                            this.tileSourceFabric.Get(layerName),
                            backgroundColor);
                    }
                }

                var imageFormat = ImageHelper.ImageFormatFromMediaType(format);
                var imageData = ImageHelper.SaveImageToByteArray(resultImage, imageFormat);

                return imageData;
            }
        }

        private static async Task DrawLayerAsync(
            int width, Models.Bounds boundingBox,
            Bitmap resultImage,
            TileSources.ITileSource source,
            int backgroundColor)
        {
            var tileCoordinates = WmsHelper.BuildTileCoordinatesList(boundingBox, width);
            var sourceTiles = await GetSourceTilesAsync(source, tileCoordinates);
            if (sourceTiles.Count > 0)
            {
                WmsHelper.DrawTilesToRasterCanvas(resultImage, boundingBox, sourceTiles, backgroundColor, WebMercator.TileSize);
            }
        }

        private string BaseUrl
        {
            get
            {
                return $"{this.Request.Scheme}://{this.Request.Host}{this.Request.PathBase}";
            }
        }

        private static async Task<List<Models.TileDataset>> GetSourceTilesAsync(
            TileSources.ITileSource source,
            IList<Models.TileCoordinates> tileCoordinates)
        {
            var sourceTiles = new List<Models.TileDataset>(tileCoordinates.Count);
            foreach (var tc in tileCoordinates)
            {
                // 180 degrees
                var tileCount = WebMercator.TileCount(tc.Z);
                var tileX = tc.X % tileCount;

                var tileData = await source.GetTileAsync(tileX, WebMercator.FlipYCoordinate(tc.Y, tc.Z), tc.Z);
                if (tileData != null)
                {
                    sourceTiles.Add(new Models.TileDataset(tc.X, tc.Y, tc.Z, tileData));
                }
            }

            return sourceTiles;
        }
    }
}
