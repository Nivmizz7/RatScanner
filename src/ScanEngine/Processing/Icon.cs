using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using RatStash;
using Tesseract;

namespace RatEye.Processing
{
    /// <summary>
    /// Represents an icon in the inventory
    /// </summary>
    public class Icon : IDisposable
    {
        private const float OcrVerificationThreshold = 0.7f;
        private static readonly Regex OcrShortNameSanitizer = new(@"[^\p{L}\p{N} \-.]");

        private readonly Config _config;
        private readonly Bitmap _icon;
        private Bitmap _scaledIcon;
        private Item _item;
        private ItemExtraInfo _itemExtraInfo;
        private float _detectionConfidence;
        private Vector2 _itemPosition;
        private bool _rotated;
        private string _ocrTitle = "";
        private bool _disposed;

        private Config.Processing ProcessingConfig => _config.ProcessingConfig;
        private Config.Path PathConfig => _config.PathConfig;
        private Config.Processing.Icon IconConfig => ProcessingConfig.IconConfig;

        /// <summary>
        /// Sync object to synchronize control flow of multiple threads
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Position of the icon inside the inventory (top, left)
        /// </summary>
        public Vector2 Position { get; }

        /// <summary>
        /// Size of the icon image, measured in pixel
        /// </summary>
        /// <remarks>
        /// This might not be the exact size of the item and does not account for rotation
        /// </remarks>
        public Vector2 Size { get; }

        /// <summary>
        /// Size of the item, measured in pixels
        /// </summary>
        public Vector2 ItemSize
        {
            get
            {
                var size = IconSlotSize();
                size.X = (int)(size.X * ProcessingConfig.ScaledSlotSize);
                size.Y = (int)(size.Y * ProcessingConfig.ScaledSlotSize);
                return size;
            }
        }

        /// <summary>
        /// The detected item
        /// </summary>
        public Item Item
        {
            get
            {
                SatisfyState(State.Scanned);
                return _item;
            }
        }

        /// <summary>
        /// The detected item extra info
        /// </summary>
        public ItemExtraInfo ItemExtraInfo
        {
            get
            {
                SatisfyState(State.Scanned);
                return _itemExtraInfo;
            }
        }

        /// <summary>
        /// The path to the icon of the detected item
        /// </summary>
        public string IconPath =>
            Item == null ? null : _config.IconManager.GetIconPath(Item, ItemExtraInfo ?? new ItemExtraInfo());

        /// <summary>
        /// Confidence with which the <see cref="Item"/> was detected/>
        /// </summary>
        public float DetectionConfidence
        {
            get
            {
                SatisfyState(State.Scanned);
                return _detectionConfidence;
            }
        }

        /// <summary>
        /// The exact position of the item
        /// </summary>
        public Vector2 ItemPosition
        {
            get
            {
                SatisfyState(State.Scanned);
                return _itemPosition;
            }
        }

        /// <summary>
        /// <see langword="true"/> if the icon is rotated
        /// </summary>
        public bool Rotated
        {
            get
            {
                SatisfyState(State.Scanned);
                return _rotated;
            }
        }

        internal Icon(Bitmap icon, Vector2 position, Vector2 size, Config config)
        {
            _config = config;
            _icon = icon;
            Position = position;
            Size = size;
        }

        private enum State
        {
            Default,
            Rescaled,
            Scanned,
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
                    case State.Rescaled:
                        RescaleIcon();
                        break;
                    case State.Scanned:
                        if (IconConfig.ScanMode == Config.Processing.Icon.ScanModes.TemplateMatching)
                        {
                            TemplateMatch();
                            if (IconConfig.ScanRotatedIcons)
                                TemplateMatch(true);
                            VerifyLowConfidenceTemplateMatchWithOcr();
                        }
                        else if (IconConfig.ScanMode == Config.Processing.Icon.ScanModes.OCR)
                            OCR();
                        break;
                    default:
                        throw new Exception("Cannot satisfy unknown state.");
                }

                _currentState++;
            }
        }

        private void RescaleIcon()
        {
            Logger.LogDebugBitmap(_icon, "icon/_icon");
            var mul = IconConfig.ScanMode == Config.Processing.Icon.ScanModes.OCR ? 2 : 1;
            _scaledIcon = _icon.Rescale(ProcessingConfig.InverseScale * mul);
            Logger.LogDebugBitmap(_scaledIcon, "icon/_scaledIcon");
        }

        private void TemplateMatch(bool rotated = false)
        {
            SatisfyState(State.Rescaled);

            // NOTE The source image is scaled hence all outgoing pixel values need to be adjusted accordingly
            using var source = _scaledIcon.ToMat();
            if (rotated)
                Cv2.Rotate(source, source, RotateFlags.Rotate90Counterclockwise);

            Logger.LogDebugMat(source, "icon/source");

            (string match, float confidence, Vector2 pos) staticResult = default;
            (string match, float confidence, Vector2 pos) dynamicResult = default;

            if (!IconConfig.UseStaticIcons)
            {
                throw new Exception(
                    "No icons for template matching can be used." + nameof(IconConfig.UseStaticIcons) + " is false."
                );
            }

            var iconManager = _config.IconManager;
            var iconSlotSize = IconSlotSize();
            var slotSize = rotated ? new Vector2(iconSlotSize.Y, iconSlotSize.X) : iconSlotSize;
            if (IconConfig.UseStaticIcons)
            {
                iconManager.EnsureStaticIconsLoaded(slotSize);
                if (iconManager.StaticIcons.ContainsKey(slotSize))
                {
                    iconManager.StaticIconsLock.EnterReadLock();
                    try
                    {
                        staticResult = TemplateMatchSub(source, iconManager.StaticIcons[slotSize]);
                    }
                    finally
                    {
                        iconManager.StaticIconsLock.ExitReadLock();
                    }
                }
            }

            var (match, confidence, pos) =
                staticResult.confidence > dynamicResult.confidence ? staticResult : dynamicResult;

            if (!(confidence > _detectionConfidence))
                return;

            _rotated = rotated;
            _itemPosition = (rotated ? new(pos.Y, pos.X) : pos) * _config.ProcessingConfig.Scale;
            _detectionConfidence = confidence;
            _item = _config.IconManager.GetItem(match);
            _itemExtraInfo = _config.IconManager.GetItemExtraInfo(match);
        }

        private (string match, float confidence, Vector2 pos) TemplateMatchSub(
            Mat source,
            Dictionary<string, Mat> icons
        )
        {
            var bestMatch = "";
            var confidence = 0f;
            var position = Vector2.Zero;

            Parallel.ForEach(
                icons,
                icon =>
                {
                    using var matches = source.MatchTemplate(icon.Value, TemplateMatchModes.SqDiffNormed);
                    matches.MinMaxLoc(out var minVal, out _, out var minLoc, out _);

                    lock (_sync)
                    {
                        minVal = 1 - minVal;
                        if (!(minVal > confidence))
                            return;
                        confidence = (float)minVal;
                        bestMatch = icon.Key;
                        position = new Vector2(minLoc);
                        //Logger.LogDebugMat(icon.Value, $"icon/conf-{confidence}.png");
                        //Logger.LogDebugMat(matches, $"icon/match-conf-{confidence}.png");
                    }
                }
            );

            return (bestMatch, confidence, position);
        }

        /// <summary>
        /// Converts the pixel unit of the icon into the slot unit
        /// </summary>
        /// <returns>Slot size of the icon</returns>
        private Vector2 IconSlotSize()
        {
            // Use converter class to round to nearest int instead of always rounding down
            var x = (Size.X - 1) / ProcessingConfig.ScaledSlotSize;
            var y = (Size.Y - 1) / ProcessingConfig.ScaledSlotSize;
            return new Vector2((int)x, (int)y);
        }

        /// <summary>
        /// Perform optical character recognition on the scaled icon image.
        /// </summary>
        private void OCR()
        {
            var topCutoff = 0;
            var leftCutoff = 4;

            // Setup tesseract
            using var scaledIconMat = _scaledIcon.ToMat();
            Logger.LogDebugMat(scaledIconMat);
            using var topText = _scaledIcon.Crop(leftCutoff, topCutoff, _scaledIcon.Width - leftCutoff, 24);
            using var topTextMat = topText.ToMat();

            // Gray scale image
            //Logger.LogDebug("Gray scaling...");
            //var cvu83 = topTextMat.CvtColor(ColorConversionCodes.BGR2GRAY, 1);
            //Logger.LogDebugMat(cvu83);

            // Binarize image
            //Logger.LogDebug("Binarizing...");
            //cvu83 = cvu83.Threshold(120, 255, ThresholdTypes.BinaryInv);
            //Logger.LogDebugMat(cvu83);

            using var hsv = topTextMat.CvtColor(ColorConversionCodes.BGR2HSV_FULL);
            using var colorFilter = hsv.InRange(
                new Scalar(93 * (255f / 180f), 16, 97),
                new Scalar(113 * (255f / 180f), 24, 217)
            );

            using var morphologyStructure = new Mat(2, 2, MatType.CV_8U, Scalar.All(1));
            Cv2.MorphologyEx(colorFilter, colorFilter, MorphTypes.Close, morphologyStructure);
            Cv2.BitwiseNot(colorFilter, colorFilter);
            Logger.LogDebugMat(colorFilter);

            using var filteredBitmap = colorFilter.ToBitmap();
            Bitmap final = filteredBitmap.Rescale(2);
            try
            {
                Logger.LogDebugBitmap(final);

                // Convert to Pix
                using var pix = PixConverter.ToPix(final);

                // OCR
                Logger.LogDebug("Applying OCR...");
                using var result = GetTesseractEngine().Process(pix);
                var text = result.GetText();

                var r = result.GetSegmentedRegions(PageIteratorLevel.TextLine);
                foreach (var x in r)
                {
                    Logger.LogDebug("TEXT: " + x);
                }
                Logger.LogDebugMat(topTextMat, "lalalla");

                var rgx = new Regex("[^a-zA-Z0-9 -\\.]");
                _itemPosition = Vector2.Zero;
                _ocrTitle = rgx.Replace(text.CyrillicToLatin().Trim(), "").Trim();
                Logger.LogDebug("Read: " + _ocrTitle);
                SetOCRItem();
            }
            finally
            {
                if (!ReferenceEquals(final, filteredBitmap))
                    final.Dispose();
            }
        }

        private void VerifyLowConfidenceTemplateMatchWithOcr()
        {
            if (_detectionConfidence >= OcrVerificationThreshold)
                return;

            var langCode = ProcessingConfig.Language.ToISO3Code();
            var trainedDataPath = System.IO.Path.Combine(PathConfig.TrainedData, $"{langCode}.traineddata");
            if (!System.IO.File.Exists(trainedDataPath))
                return;

            Bitmap ocrIcon = _icon.Rescale(ProcessingConfig.InverseScale * 2);
            try
            {
                var titleHeight = Math.Min(
                    ocrIcon.Height,
                    (int)Math.Round(ProcessingConfig.BaseSlotSize * (40f / 63f))
                );
                var titleLeft = Math.Min(ocrIcon.Width - 1, (int)Math.Floor(ocrIcon.Width * 0.55f));
                using var title = ocrIcon.Crop(titleLeft, 0, ocrIcon.Width - titleLeft, titleHeight);
                using var titleMat = title.ToMat();
                using var gray =
                    titleMat.Channels() == 1 ? titleMat.Clone() : titleMat.CvtColor(ColorConversionCodes.BGR2GRAY);
                using var binary = gray.Threshold(110, 255, ThresholdTypes.Binary);
                Cv2.BitwiseNot(binary, binary);
                using var enlarged = binary.Resize(new OpenCvSharp.Size(), 3, 3, InterpolationFlags.Cubic);
                using var filteredBitmap = enlarged.ToBitmap();
                using var pix = PixConverter.ToPix(filteredBitmap);

                string text;
                var tesseractEngine = GetTesseractEngine();
                lock (tesseractEngine)
                {
                    using var result = tesseractEngine.Process(pix, PageSegMode.SingleLine);
                    text = result.GetText();
                }

                var slotSize = IconSlotSize();
                var items = _config.RatStashDB.GetItems(item =>
                {
                    var size = new Vector2(item.GetSlotSize());
                    return size == slotSize || size == slotSize.Flipped;
                });
                var verifiedItem = FindUniqueExactShortName(items, text);
                if (verifiedItem == null)
                    return;

                _item = verifiedItem;
                _itemExtraInfo = null;
                _detectionConfidence = 1;
                _rotated = new Vector2(verifiedItem.GetSlotSize()) != slotSize;
                Logger.LogDebug(
                    $"Verified low-confidence template match as '{verifiedItem.ShortName}' using icon title OCR."
                );
            }
            finally
            {
                if (!ReferenceEquals(ocrIcon, _icon))
                    ocrIcon.Dispose();
            }
        }

        internal static Item FindUniqueExactShortName(IEnumerable<Item> items, string ocrText)
        {
            var normalizedText = NormalizeOcrShortName(ocrText);
            if (string.IsNullOrWhiteSpace(normalizedText))
                return null;

            var matches = items.Where(item => NormalizeOcrShortName(item.ShortName) == normalizedText).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        internal static string NormalizeOcrShortName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return OcrShortNameSanitizer.Replace(value.CyrillicToLatin().Trim(), "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Set the item to one, best matching the scanned title
        /// </summary>
        private void SetOCRItem()
        {
            var slotSize = IconSlotSize();
            var items = _config.RatStashDB.GetItems(i =>
            {
                var v = new Vector2(i.GetSlotSize());
                return v == slotSize || v == slotSize.Flipped;
            });
            _item = null;
            _detectionConfidence = 0;
            if (string.IsNullOrWhiteSpace(_ocrTitle))
                return;

            foreach (Item item in items)
            {
                float confidence = item
                    .ShortName.Replace("I", "T")
                    .CyrillicToLatin()
                    .NormedLevenshteinDistance(_ocrTitle);
                if (confidence <= _detectionConfidence)
                    continue;

                _item = item;
                _detectionConfidence = confidence;
            }
            if (_item == null)
                return;
            _rotated = new Vector2(_item.GetSlotSize()) != slotSize;
        }

        /// <summary>
        /// Creates an instance of the OCRTesseract class. Initializes Tesseract.
        /// </summary>
        /// <returns>Tesseract instance trained for the bender font</returns>
        private TesseractEngine GetTesseractEngine()
        {
            // Return if tesseract instance was already created
            var tesseractEngine = IconConfig.TesseractEngine;
            if (tesseractEngine != null)
                return tesseractEngine;

            // Check if trained data is present
            var langCode = _config.ProcessingConfig.Language.ToISO3Code();
            var traineddataPath = $"{PathConfig.TrainedData}\\{langCode}.traineddata";
            if (!System.IO.File.Exists(traineddataPath))
            {
                var message = "Could not find traineddata at: " + traineddataPath;
                throw new System.IO.FileNotFoundException(message, traineddataPath);
            }

            // Load additional language to expand the primary one
            var addLang = _config.ProcessingConfig.Language switch
            {
                //Language.Chinese => "eng",
                Language.Czech => "+eng",
                Language.Japanese => "+eng",
                Language.Korean => "+eng",
                Language.Russian => "+eng",
                _ => "",
            };

            var language = langCode + addLang;

            // Create a tesseract instance
            IconConfig.TesseractEngine = new TesseractEngine(PathConfig.TrainedData, language, EngineMode.LstmOnly)
            {
                DefaultPageSegMode = PageSegMode.RawLine,
            };

            return IconConfig.TesseractEngine;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_scaledIcon != null && !ReferenceEquals(_scaledIcon, _icon))
                _scaledIcon.Dispose();
            _icon.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
