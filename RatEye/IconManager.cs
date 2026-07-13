using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using RatEye.Properties;
using RatStash;
using Color = RatStash.Color;

namespace RatEye
{
    internal class IconManager : IDisposable
    {
        private const long MaxCacheBytes = 256L * 1024 * 1024;
        private const int MaxCacheFiles = 10_000;
        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan MaxTemporaryCacheFileAge = TimeSpan.FromDays(1);

        private enum IconType
        {
            Static,
            Dynamic,
        }

        private readonly Config _config;
        private readonly string _cacheDirectory;

        /// <summary>
        /// Static icons are those which are rendered ahead of time.
        /// For example keys, medical supply's, containers, standalone mods,
        /// and especially items like screws, drill, wires, milk and so on.
        /// <para/>
        /// <c>Dictionary&lt;slotSize, Dictionary&lt;iconKey, icon&gt;&gt;</c>
        /// </summary>
        /// <remarks>
        /// Use the <see cref="StaticIconsLock"/> when accessing this collection.
        /// Icon is of type 8UC3.
        /// </remarks>
        internal Dictionary<Vector2, Dictionary<string, Mat>> StaticIcons = new();

        /// <summary>
        /// Dynamic icons are those which need to be rendered at runtime
        /// due to the items appearance being altered by attached items.
        /// For example weapons are considered dynamic items since their
        /// icon changes when you add rails, magazines, scopes and so on.
        /// <para/>
        /// <c>ConcurrentDictionary&lt;slotSize, Dictionary&lt;iconKey, icon&gt;&gt;</c>
        /// </summary>
        /// <remarks>
        /// Use the <see cref="DynamicIconsLock"/> when accessing this collection.
        /// Icon is of type 8UC3.
        /// </remarks>
        internal Dictionary<Vector2, Dictionary<string, Mat>> DynamicIcons = new();

        /// <summary>
        /// Reader / Writer lock of <see cref="StaticIcons"/>
        /// </summary>
        internal readonly ReaderWriterLockSlim StaticIconsLock = new();

        /// <summary>
        /// Reader / Writer lock of <see cref="DynamicIcons"/>
        /// </summary>
        internal readonly ReaderWriterLockSlim DynamicIconsLock = new();

        /// <summary>
        /// The icon paths connected to each icon key
        /// <para/> ConcurrentDictionary&lt;iconKey, iconPath&gt;
        /// </summary>
        private readonly Dictionary<string, string> _iconPaths = new();

        /// <summary>
        /// The data used to match icon keys of static icons to their item
        /// <para/>
        /// <c>Dictionary&lt;iconKey, item&gt;</c>
        /// </summary>
        private Dictionary<string, Item> _staticCorrelationData = new();

        /// <summary>
        /// The data used to match icon keys of dynamic icons to their item
        /// <para/>
        /// <c>Dictionary&lt;iconKey, item&gt;</c>
        /// </summary>
        private readonly Dictionary<string, (Item, ItemExtraInfo)> _dynamicCorrelationData = new();

        /// <summary>
        /// Reader / Writer lock of <see cref="_staticCorrelationDataLock"/>
        /// </summary>
        private readonly ReaderWriterLockSlim _staticCorrelationDataLock = new();

        /// <summary>
        /// Reader / Writer lock of <see cref="_dynamicCorrelationDataLock"/>
        /// </summary>
        private readonly ReaderWriterLockSlim _dynamicCorrelationDataLock = new();
        private readonly object _staticIconLoadLock = new();
        private readonly HashSet<Vector2> _loadedStaticIconSizes = new();
        private bool _disposed;

        /// <summary>
        /// Constructor for icon manager object
        /// </summary>
        /// <param name="config">The config to use for this instance></param>
        /// <remarks>Depends on <see cref="Config.Processing.Icon"/> and <see cref="Config.Path"/></remarks>
        internal IconManager(Config config)
            : this(config, config.PathConfig.CacheDir) { }

        internal IconManager(Config config, string cacheDirectory)
        {
            _config = config;
            _cacheDirectory = cacheDirectory;

            if (_config.ProcessingConfig.UseCache)
            {
                Directory.CreateDirectory(_cacheDirectory);
                PruneCacheBestEffort();
            }

            var iconConfig = _config.ProcessingConfig.IconConfig;
            if (iconConfig.UseStaticIcons)
                LoadStaticCorrelationData();
        }

        #region Icon loading

        internal void EnsureStaticIconsLoaded(Vector2 slotSize)
        {
            lock (_staticIconLoadLock)
            {
                if (_loadedStaticIconSizes.Contains(slotSize))
                    return;

                Dictionary<Vector2, Dictionary<string, Mat>> newIcons;
                try
                {
                    newIcons = LoadNewIcons(_config.PathConfig.StaticIcons, IconType.Static, slotSize);
                }
                catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException)
                {
                    // Missing Data/icons is a recoverable packaging issue; keep scanning alive.
                    Logger.LogDebug(
                        "Static icon folder is missing; icon matching for this slot size will stay empty until data is installed.",
                        e
                    );
                    _loadedStaticIconSizes.Add(slotSize);
                    return;
                }

                StaticIconsLock.EnterWriteLock();
                try
                {
                    foreach (var icons in newIcons)
                    {
                        if (!StaticIcons.ContainsKey(icons.Key))
                            StaticIcons.Add(icons.Key, new Dictionary<string, Mat>());
                        foreach (var icon in icons.Value)
                            StaticIcons[icons.Key].Add(icon.Key, icon.Value);
                    }

                    _loadedStaticIconSizes.Add(slotSize);
                }
                finally
                {
                    StaticIconsLock.ExitWriteLock();
                }
            }
        }

        private Dictionary<Vector2, Dictionary<string, Mat>> LoadNewIcons(
            string folderPath,
            IconType iconType,
            Vector2 slotSizeFilter = null,
            int retryCount = 0
        )
        {
            if (!Directory.Exists(folderPath))
            {
                var message = "Could not find icon folder at: " + folderPath;
                throw new FileNotFoundException(message);
            }

            var loadedIcons = new Dictionary<Vector2, Dictionary<string, Mat>>();
            try
            {
                var iconPathArray = Directory.GetFiles(folderPath, "*.png");
                if (iconType == IconType.Static)
                {
                    _staticCorrelationDataLock.EnterReadLock();
                    StaticIconsLock.EnterReadLock();
                }
                else if (iconType == IconType.Dynamic)
                {
                    _dynamicCorrelationDataLock.EnterReadLock();
                    DynamicIconsLock.EnterReadLock();
                }
                try
                {
                    var configHash = GetConfigHash();

                    Parallel.ForEach(
                        iconPathArray,
                        iconPath =>
                        {
                            var iconKey = GetIconKey(iconPath, iconType);

                            var item = GetItemUnsafe(iconKey);
                            if (item == null)
                                return;
                            if (slotSizeFilter != null && new Vector2(item.GetSlotSize()) != slotSizeFilter)
                                return;

                            // Skip existing icons
                            if (iconType == IconType.Static && StaticIcons.Any(x => x.Value.ContainsKey(iconKey)))
                                return;
                            if (iconType == IconType.Dynamic && DynamicIcons.Any(x => x.Value.ContainsKey(iconKey)))
                                return;

                            var useCache = _config.ProcessingConfig.UseCache;
                            var cacheIconPath = Path.Combine(_cacheDirectory, $"{iconKey.CacheKey(configHash)}.bmp");
                            var icon = useCache ? TryLoadCachedIcon(cacheIconPath) : null;
                            var cacheHit = icon != null;

                            if (!cacheHit)
                            {
                                using var mat = Cv2.ImRead(iconPath, ImreadModes.Unchanged);
                                icon = GetIconWithBackground(mat, item);
                            }

                            // Do not add the icon to the list, if its size cannot be converted to slots
                            if (!IsValidPixelSize(icon.Width) || !IsValidPixelSize(icon.Height))
                            {
                                icon.Dispose();
                                return;
                            }

                            // Add the icon to the cache if caching is enabled and doesn't already contain it
                            if (useCache && !cacheHit)
                                SaveCacheIconAtomic(icon, cacheIconPath);

                            var size = new Vector2(PixelsToSlots(icon.Width), PixelsToSlots(icon.Height));
                            lock (loadedIcons)
                            {
                                if (!loadedIcons.ContainsKey(size))
                                {
                                    loadedIcons.Add(size, new Dictionary<string, Mat>());
                                }

                                // Add icon to icon and path dictionary
                                loadedIcons[size][iconKey] = icon;
                                _iconPaths[iconKey] = cacheHit ? cacheIconPath : iconPath;
                            }
                        }
                    );
                }
                finally
                {
                    if (iconType == IconType.Static)
                    {
                        _staticCorrelationDataLock.ExitReadLock();
                        StaticIconsLock.ExitReadLock();
                    }
                    else if (iconType == IconType.Dynamic)
                    {
                        _dynamicCorrelationDataLock.ExitReadLock();
                        DynamicIconsLock.ExitReadLock();
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug("Could not load icons!", e);
                if (retryCount > 0)
                {
                    Thread.Sleep(100);
                    return LoadNewIcons(folderPath, iconType, slotSizeFilter, retryCount - 1);
                }
            }

            if (_config.ProcessingConfig.UseCache)
                PruneCacheBestEffort();

            return loadedIcons;
        }

        private Mat TryLoadCachedIcon(string cacheIconPath)
        {
            if (!File.Exists(cacheIconPath))
                return null;

            Mat icon = null;
            try
            {
                icon = Cv2.ImRead(cacheIconPath, ImreadModes.Unchanged);
                var valid =
                    !icon.Empty()
                    && icon.Type() == MatType.CV_8UC3
                    && IsValidPixelSize(icon.Width)
                    && IsValidPixelSize(icon.Height);
                if (valid)
                    return icon;

                icon.Dispose();
                icon = null;
                DeleteCacheFileBestEffort(cacheIconPath);
                Logger.LogDebug("Regenerating invalid cached icon: " + cacheIconPath);
                return null;
            }
            catch (Exception e)
            {
                icon?.Dispose();
                DeleteCacheFileBestEffort(cacheIconPath);
                Logger.LogDebug("Regenerating unreadable cached icon: " + cacheIconPath, e);
                return null;
            }
        }

        private static void SaveCacheIconAtomic(Mat icon, string cacheIconPath)
        {
            var cacheDirectory = Path.GetDirectoryName(cacheIconPath);
            var temporaryPath = Path.Combine(
                cacheDirectory,
                $"{Path.GetFileNameWithoutExtension(cacheIconPath)}.{Guid.NewGuid():N}.tmp.bmp"
            );

            try
            {
                Directory.CreateDirectory(cacheDirectory);
                icon.SaveImage(temporaryPath);
                if (new FileInfo(temporaryPath).Length == 0)
                    throw new InvalidDataException("The encoded cache image was empty.");

                if (File.Exists(cacheIconPath))
                {
                    File.Replace(temporaryPath, cacheIconPath, null, true);
                }
                else
                {
                    try
                    {
                        File.Move(temporaryPath, cacheIconPath);
                    }
                    catch (IOException) when (File.Exists(cacheIconPath))
                    {
                        // Another process published the same complete cache entry first.
                    }
                }
            }
            catch (Exception e)
            {
                // Cache persistence must never prevent the in-memory icon from being used.
                Logger.LogDebug("Could not persist cached icon: " + cacheIconPath, e);
            }
            finally
            {
                DeleteCacheFileBestEffort(temporaryPath);
            }
        }

        private void PruneCacheBestEffort()
        {
            PruneCache(_cacheDirectory, DateTime.UtcNow, MaxCacheAge, MaxCacheBytes, MaxCacheFiles);
        }

        internal static void PruneCache(
            string cacheDirectory,
            DateTime utcNow,
            TimeSpan maxAge,
            long maxBytes,
            int maxFiles
        )
        {
            try
            {
                if (!Directory.Exists(cacheDirectory))
                    return;

                var cacheFiles = new List<(FileInfo file, long length)>();
                var staleCutoff = utcNow - maxAge;
                var temporaryCutoff = utcNow - MaxTemporaryCacheFileAge;

                foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.bmp", SearchOption.TopDirectoryOnly))
                {
                    var file = new FileInfo(path);
                    var isTemporary = file.Name.EndsWith(".tmp.bmp", StringComparison.OrdinalIgnoreCase);
                    if (
                        (isTemporary && file.LastWriteTimeUtc <= temporaryCutoff)
                        || file.LastWriteTimeUtc <= staleCutoff
                    )
                    {
                        DeleteCacheFileBestEffort(path);
                        continue;
                    }

                    if (!isTemporary)
                        cacheFiles.Add((file, file.Length));
                }

                var totalBytes = cacheFiles.Sum(entry => entry.length);
                var fileCount = cacheFiles.Count;
                foreach (var entry in cacheFiles.OrderBy(entry => entry.file.LastWriteTimeUtc))
                {
                    if (totalBytes <= maxBytes && fileCount <= maxFiles)
                        break;
                    if (!DeleteCacheFileBestEffort(entry.file.FullName))
                        continue;

                    totalBytes -= entry.length;
                    fileCount--;
                }
            }
            catch (Exception e)
            {
                // Cleanup is opportunistic; cache failures must not block scanning.
                Logger.LogDebug("Could not prune the icon cache.", e);
            }
        }

        private static bool DeleteCacheFileBestEffort(string path)
        {
            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generate the background of an item as it would appear in game
        /// </summary>
        /// <param name="transparentIcon">The transparent icon of the item</param>
        /// <param name="item">The item of the icon</param>
        /// <returns>8UC3 matrix of transparent icon with blended background</returns>
        private Mat GetIconWithBackground(Mat transparentIcon, Item item)
        {
            // Generate layers
            var black = new Scalar(0, 0, 0, 255);
            using var background = new Mat(transparentIcon.Size(), MatType.CV_8UC4).SetTo(black);

            using var cellStream = new MemoryStream(Resources.cell_full_border);
            using var cellBitmap = new Bitmap(cellStream);
            using var baseGridCell = cellBitmap.ToMat();
            using var gridCell = baseGridCell.Repeat(
                PixelsToSlots(transparentIcon.Width),
                PixelsToSlots(transparentIcon.Height),
                -1,
                -1
            );

            var optimizeHighlighted = _config.ProcessingConfig.InventoryConfig.OptimizeHighlighted;
            var bgColor = optimizeHighlighted ? new Color(255, 255, 255) : item.BackgroundColor.ToColor();
            var bgAlpha = _config.ProcessingConfig.InventoryConfig.BackgroundAlpha;
            var bgScalar = new Scalar(bgColor.B, bgColor.G, bgColor.R, bgAlpha);
            using var gridColor = new Mat(transparentIcon.Size(), MatType.CV_8UC4).SetTo(bgScalar);

            using var border = new Mat(transparentIcon.Size(), MatType.CV_8UC4).SetTo(new Scalar(0, 0, 0, 0));
            var borderRect = new Rect(Vector2.Zero, transparentIcon.Size());
            border.Rectangle(borderRect, _config.ProcessingConfig.InventoryConfig.GridColor);

            // Blend layers
            using var blendedGrid = gridCell.AlphaBlend(background);
            using var tmp1 = blendedGrid.RemoveTransparency();
            using var tmp2 = gridColor.AlphaBlend(tmp1);
            using var tmp3 = border.AlphaBlend(tmp2);
            using var result = transparentIcon.AlphaBlend(tmp3);

            // Add weapon mod icon
            if (item is WeaponMod)
            {
                using var weaponModIcon = GetWeaponModIcon(item);
                if (weaponModIcon != null)
                {
                    var top = result.Height - weaponModIcon.Height - 2;
                    var right = result.Width - weaponModIcon.Width - 2;
                    using var weaponModIconPadded = weaponModIcon.AddPadding(2, top, right, 2);
                    using var resultWithMod = weaponModIconPadded.AlphaBlend(result);
                    return resultWithMod.CvtColor(ColorConversionCodes.BGRA2BGR, 3);
                }
            }

            // Convert to 8UC3 and return
            return result.CvtColor(ColorConversionCodes.BGRA2BGR, 3);
        }

        private Mat GetWeaponModIcon(Item item)
        {
            var bg = GetWeaponModIconBackground(item);
            using var fg = GetWeaponModIconForeground(item);
            if (fg == null)
                return bg;

            using (bg)
            {
                using var paddedBg = bg.AddPadding(bg.Width, bg.Height, bg.Width, bg.Height);

                var hPadding = (bg.Width * 3) - fg.Width;
                var vPadding = (bg.Height * 3) - fg.Height;

                var left = hPadding / 2 + hPadding % 2;
                var top = vPadding / 2 + vPadding % 2;
                var right = hPadding / 2;
                var bottom = vPadding / 2;
                using var paddedFg = fg.AddPadding(left, top, right, bottom);

                using var blended = paddedFg.AlphaBlend(paddedBg);
                return blended.RemovePadding(bg.Width, bg.Height, bg.Width, bg.Height);
            }
        }

        private Mat GetWeaponModIconBackground(Item item)
        {
            var background = item switch
            {
                EssentialMod => Resources.mod_vital,
                FunctionalMod => Resources.mod_generic,
                GearMod => Resources.mod_gear,
                _ => null,
            };
            if (background == null)
                return null;
            using var stream = new MemoryStream(background);
            using var bitmap = new Bitmap(stream);
            return bitmap.ToMat();
        }

        private Mat GetWeaponModIconForeground(Item item)
        {
            var foreground = item switch
            {
                AuxiliaryMod => Resources.icon_mod_aux,
                Barrel => Resources.icon_mod_barrel,
                Bipod => Resources.icon_mod_bipod,
                ChargingHandle => Resources.icon_mod_charge,
                Flashlight => Resources.icon_mod_flashlight,
                GasBlock => Resources.icon_mod_gasblock,
                Handguard => Resources.icon_mod_handguard,
                IronSight => Resources.icon_mod_ironsight,
                Launcher => Resources.icon_mod_launcher,
                LaserDesignator => Resources.icon_mod_lightlaser,
                Magazine => Resources.icon_mod_magazine,
                Mount => Resources.icon_mod_mount,
                MuzzleDevice => Resources.icon_mod_muzzle,
                PistolGrip => Resources.icon_mod_pistol_grip,
                RailCovers => Resources.icon_mod_railcovers,
                Receiver => Resources.icon_mod_receiver,
                Sights => Resources.icon_mod_sight,
                Stock => Resources.icon_mod_stock,
                CombTactDevice => Resources.icon_mod_tactical,
                Foregrip => Resources.icon_mod_tactical,
                _ => null,
            };
            if (foreground == null)
                return null;
            using var stream = new MemoryStream(foreground);
            using var bitmap = new Bitmap(stream);
            return bitmap.ToMat();
        }

        #endregion

        #region Correlation Data Loading

        private void LoadStaticCorrelationData()
        {
            var correlationData = new Dictionary<string, Item>();

            var iconPathArray = Directory.GetFiles(_config.PathConfig.StaticIcons, "*.png");
            foreach (var iconPath in iconPathArray)
            {
                var itemId = System.IO.Path.GetFileNameWithoutExtension(iconPath);
                var item = _config.RatStashDB.GetItem(itemId);

                // Filter out items which are not in the item database
                if (item == null)
                    continue;

                // Add the item to the correlation data
                var iconKey = GetIconKey(iconPath, IconType.Static);
                correlationData[iconKey] = item;
            }

            _staticCorrelationDataLock.EnterWriteLock();
            try
            {
                _staticCorrelationData = correlationData;
            }
            finally
            {
                _staticCorrelationDataLock.ExitWriteLock();
            }
        }

        #endregion

        /// <summary>
        /// Get the unique icon key for a icon path and its type
        /// </summary>
        /// <remarks>
        /// Keep this method coherent with <see cref="GetItem"/> and <see cref="GetItemExtraInfo"/>
        /// </remarks>
        /// <param name="iconPath">The path to the icon</param>
        /// <param name="iconType">The type of the icon</param>
        /// <returns>Unique identifier of the icon</returns>
        private string GetIconKey(string iconPath, IconType iconType)
        {
            var basePath = iconType switch
            {
                IconType.Static => _config.PathConfig.StaticIcons,
                IconType.Dynamic => _config.PathConfig.DynamicIcons,
                _ => throw new ArgumentOutOfRangeException(nameof(iconType), iconType, null),
            };
            return System.IO.Path.Combine(basePath, System.IO.Path.GetFileName(iconPath));
        }

        /// <summary>
        /// Get the item, referenced by its icon key
        /// </summary>
        /// <remarks>
        /// Keep this method coherent with <see cref="GetIconKey"/>
        /// </remarks>
        /// <param name="iconKey">The icon key</param>
        /// <returns>The matching item</returns>
        internal Item GetItem(string iconKey)
        {
            if (iconKey.StartsWith(_config.PathConfig.StaticIcons))
            {
                _staticCorrelationDataLock.EnterReadLock();
                try
                {
                    _staticCorrelationData.TryGetValue(iconKey, out var item);
                    return item;
                }
                finally
                {
                    _staticCorrelationDataLock.ExitReadLock();
                }
            }

            if (iconKey.StartsWith(_config.PathConfig.DynamicIcons))
            {
                _dynamicCorrelationDataLock.EnterReadLock();
                try
                {
                    _dynamicCorrelationData.TryGetValue(iconKey, out var item);
                    return item.Item1;
                }
                finally
                {
                    _dynamicCorrelationDataLock.ExitReadLock();
                }
            }

            return null;
        }

        /// <summary>
        /// Get the item, referenced by its icon key while assuming that
        /// <see cref="_staticCorrelationDataLock"/> or <see cref="_dynamicCorrelationDataLock"/>
        /// got correctly acquired before.
        /// </summary>
        /// <param name="iconKey">The icon key</param>
        /// <returns>The matching item</returns>
        private Item GetItemUnsafe(string iconKey)
        {
            if (iconKey.StartsWith(_config.PathConfig.StaticIcons))
            {
                _staticCorrelationData.TryGetValue(iconKey, out var item);
                return item;
            }

            if (iconKey.StartsWith(_config.PathConfig.DynamicIcons))
            {
                _dynamicCorrelationData.TryGetValue(iconKey, out var item);
                return item.Item1;
            }

            return null;
        }

        /// <summary>
        /// Get the item extra info, referenced by its icon key
        /// </summary>
        /// <remarks>
        /// Keep this method coherent with <see cref="GetIconKey"/>
        /// </remarks>
        /// <param name="iconKey">The icon key</param>
        /// <returns>The matching item extra info</returns>
        internal ItemExtraInfo GetItemExtraInfo(string iconKey)
        {
            if (iconKey.StartsWith(_config.PathConfig.DynamicIcons))
            {
                _dynamicCorrelationDataLock.EnterReadLock();
                try
                {
                    return _dynamicCorrelationData[iconKey].Item2;
                }
                finally
                {
                    _dynamicCorrelationDataLock.ExitReadLock();
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve the icon path for a item possible item extra info
        /// </summary>
        /// <param name="item">The item which icon path shall be resolved</param>
        /// <param name="itemExtraInfo">The item extra info which shall be used to further distinguish icons</param>
        /// <returns>The path to the icon of the item</returns>
        internal string GetIconPath(Item item, ItemExtraInfo itemExtraInfo)
        {
            _dynamicCorrelationDataLock.EnterReadLock();
            try
            {
                // We want First() to throw if there is no matching item
                return _dynamicCorrelationData.First(x => x.Value.Item1 == item && x.Value.Item2 == itemExtraInfo).Key;
            }
            catch
            {
                // ignored
            }
            finally
            {
                _dynamicCorrelationDataLock.ExitReadLock();
            }

            _staticCorrelationDataLock.EnterReadLock();
            try
            {
                // We want First() to throw if there is no matching item
                return _staticCorrelationData.First(entry => entry.Value == item).Key;
            }
            catch
            {
                // ignored
            }
            finally
            {
                _staticCorrelationDataLock.ExitReadLock();
            }

            return null;
        }

        /// <summary>
        /// Calculate the hash of the current config but only consider used values
        /// </summary>
        /// <returns>Hash as hex string</returns>
        private string GetConfigHash()
        {
            string configHash = new Config()
            {
                ProcessingConfig = new Config.Processing()
                {
                    // Language = _config.ProcessingConfig.Language,
                    IconConfig = new Config.Processing.Icon()
                    {
                        ScanRotatedIcons = _config.ProcessingConfig.IconConfig.ScanRotatedIcons,
                    },
                    InventoryConfig = new Config.Processing.Inventory()
                    {
                        OptimizeHighlighted = _config.ProcessingConfig.InventoryConfig.OptimizeHighlighted,
                    },
                },
            }.GetHash();
            return ("template-v2:" + configHash).SHA256Hash();
        }

        /// <summary>
        /// Converts the pixel unit of a icon into the slot unit
        /// </summary>
        /// <param name="pixels">The pixel size of the icon</param>
        /// <returns>Slot size of the icon</returns>
        private int PixelsToSlots(int pixels)
        {
            // Use converter class to round to nearest int instead of always rounding down
            return Convert.ToInt32((pixels - 1) / _config.ProcessingConfig.BaseSlotSize);
        }

        /// <summary>
        /// Checks if the give pixels can be converted into slot unit
        /// </summary>
        /// <param name="pixels">The pixel size of the icon</param>
        /// <returns>True if the pixels can be converted to slots</returns>
        private bool IsValidPixelSize(int pixels)
        {
            return Math.Abs(1 - pixels % _config.ProcessingConfig.BaseSlotSize) < 0.01f;
        }

        /// <summary>
        /// Reads a file with <see cref="FileShare.ReadWrite"/>
        /// </summary>
        /// <param name="path">The path of the file</param>
        /// <returns>The file content as string</returns>
        private static string ReadFileNonBlocking(string path)
        {
            using var fileStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var textReader = new StreamReader(fileStream);
            return textReader.ReadToEnd();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StaticIconsLock.EnterWriteLock();
            DynamicIconsLock.EnterWriteLock();
            try
            {
                foreach (Mat icon in StaticIcons.Values.SelectMany(group => group.Values))
                    icon.Dispose();
                foreach (Mat icon in DynamicIcons.Values.SelectMany(group => group.Values))
                    icon.Dispose();
                StaticIcons.Clear();
                DynamicIcons.Clear();
            }
            finally
            {
                DynamicIconsLock.ExitWriteLock();
                StaticIconsLock.ExitWriteLock();
            }

            StaticIconsLock.Dispose();
            DynamicIconsLock.Dispose();
            _staticCorrelationDataLock.Dispose();
            _dynamicCorrelationDataLock.Dispose();
            _disposed = true;
        }
    }
}
