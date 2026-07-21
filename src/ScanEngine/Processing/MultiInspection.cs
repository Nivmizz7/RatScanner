using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Point = OpenCvSharp.Point;

namespace RatEye.Processing
{
    /// <summary>
    /// Represents multiple <see cref="RatEye.Processing.Inspection"/>
    /// </summary>
    public class MultiInspection
    {
        private readonly Config _config;
        private readonly Bitmap _image;

        private Config.Path PathConfig => _config.PathConfig;
        private Config.Processing ProcessingConfig => _config.ProcessingConfig;
        private Config.Processing.Inspection InspectionConfig => ProcessingConfig.InspectionConfig;

        // Backing property fields
        private List<Inspection> _inspections;

        /// <summary>
        /// List of all inspections found in the image
        /// </summary>
        public List<Inspection> Inspections
        {
            get
            {
                SatisfyState(State.SearchedMarkers);
                return _inspections;
            }
            private set => _inspections = value;
        }

        /// <summary>
        /// Constructor for MultiInspection view processing object
        /// </summary>
        /// <param name="image">Image of the multiInspection view which will be processed</param>
        /// <param name="config">The config to use for this instance></param>
        /// <remarks>Provided image has to be in RGB</remarks>
        internal MultiInspection(Bitmap image, Config config)
        {
            _config = config;
            _image = image;
        }

        #region Processing state handling

        private enum State
        {
            Default,
            SearchedMarkers,
        }

        private State _currentState = State.Default;

        private void SatisfyState(State targetState)
        {
            while (_currentState < targetState)
            {
                switch (_currentState + 1)
                {
                    case State.Default:
                        break;
                    case State.SearchedMarkers:
                        SearchMarker();
                        break;
                    default:
                        throw new Exception("Cannot satisfy unknown state.");
                }

                _currentState++;
            }
        }

        #endregion

        /// <summary>
        /// Search for all different marker types and pick the best matching one
        /// </summary>
        private void SearchMarker()
        {
            SatisfyState(State.Default);

            using Bitmap marker = GetScaledMarker();
            var markers = GetMarkerPositions(marker);
            _inspections = markers
                .Select(match => new Inspection(_image, _config, match.position, match.confidence))
                .ToList();
        }

        /// <summary>
        /// Identify the give marker inside the source
        /// </summary>
        /// <param name="marker">The marker template to identify</param>
        /// <remarks>Provided marker has to be in RGB</remarks>
        /// <returns>List of markers which confidence is above <see cref="Config.Processing.Inspection.MarkerThreshold"/></returns>
        private List<(Vector2 position, float confidence)> GetMarkerPositions(Bitmap marker)
        {
            using var refMat = _image.ToMat();
            using var tplMat = marker.ToMat(); // tpl = template
            using var res = new Mat(refMat.Rows - tplMat.Rows + 1, refMat.Cols - tplMat.Cols + 1, MatType.CV_32FC1);

            // Gray scale both reference and template image
            using var gref = refMat.CvtColor(ColorConversionCodes.RGB2GRAY);
            using var gtpl = tplMat.CvtColor(ColorConversionCodes.RGB2GRAY);

            Cv2.MatchTemplate(gref, gtpl, res, TemplateMatchModes.CCoeffNormed);

            return ExtractMarkerPeaks(res, marker.Size, InspectionConfig.MarkerThreshold);
        }

        internal static List<(Vector2 position, float confidence)> ExtractMarkerPeaks(
            Mat response,
            System.Drawing.Size markerSize,
            float threshold
        )
        {
            var matches = new List<(Vector2 position, float confidence)>();
            while (true)
            {
                Cv2.MinMaxLoc(response, out _, out double maxValue, out _, out Point maxLocation);
                if (maxValue < threshold)
                    break;

                matches.Add((new Vector2(maxLocation), (float)maxValue));

                int left = Math.Max(0, maxLocation.X - markerSize.Width / 2);
                int top = Math.Max(0, maxLocation.Y - markerSize.Height / 2);
                int right = Math.Min(response.Width, maxLocation.X + markerSize.Width / 2 + 1);
                int bottom = Math.Min(response.Height, maxLocation.Y + markerSize.Height / 2 + 1);
                using Mat suppressionRegion = response[new Rect(left, top, right - left, bottom - top)];
                suppressionRegion.SetTo(Scalar.All(-1));
            }

            return matches;
        }

        /// <summary>
        /// Generate a marker bitmap
        /// </summary>
        /// <remarks><see cref="Config.Processing.Scale"/> is already accounted for.</remarks>
        /// <returns>A rescaled and alpha blended version of <see cref="Config.Processing.Inspection.Marker"/></returns>
        private Bitmap GetScaledMarker()
        {
            Bitmap output = InspectionConfig.Marker.Rescale(InspectionConfig.MarkerItemScale * ProcessingConfig.Scale);
            try
            {
                return output.TransparentToColor(InspectionConfig.MarkerBackgroundColor);
            }
            finally
            {
                if (!ReferenceEquals(output, InspectionConfig.Marker))
                    output.Dispose();
            }
        }
    }
}
