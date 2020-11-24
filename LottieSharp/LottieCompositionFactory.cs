﻿using LottieSharp.Model;
using LottieSharp.Network;
using LottieSharp.Parser;
using LottieSharp.Utils;
using SharpDX.Direct2D1;
using SharpDX.WIC;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace LottieSharp
{
    /// <summary>
    /// Helpers to create or cache a LottieComposition.
    /// All factory methods take a cache key. The animation will be stored in an LRU cache for future use.
    /// In-progress tasks will also be held so they can be returned for subsequent requests for the same
    /// animation prior to the cache being populated.
    /// </summary>
    public static class LottieCompositionFactory
    {
        static ImagingFactory imagingFactory = new ImagingFactory();

        /// <summary>
        /// Keep a map of cache keys to in-progress tasks and return them for new requests. 
        /// Without this, simultaneous requests to parse a composition will trigger multiple parallel 
        /// parse tasks prior to the cache getting populated. 
        /// </summary>
        private static readonly Dictionary<string, Task<LottieResult<LottieComposition>>> _taskCache = new Dictionary<string, Task<LottieResult<LottieComposition>>>();

        static LottieCompositionFactory()
        {
            Utils.Utils.DpScale();
        }

        /// <summary>
        /// Fetch an animation from an http url. Once it is downloaded once, Lottie will cache the file to disk for
        /// future use. Because of this, you may call <seealso cref="FromUrl(Context, string)"/> ahead of time to warm the cache if you think you
        /// might need an animation in the future.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="url"></param>
        /// <returns></returns>
        public static async Task<LottieResult<LottieComposition>> FromUrlAsync(RenderTarget renderTarget, string url, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await NetworkFetcher.FetchAsync(renderTarget, url, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Parse an animation from src/main/assets. It is recommended to use {@link #fromRawRes(Context, int)} instead.
        /// The asset file name will be used as a cache key so future usages won't have to parse the json again.
        /// However, if your animation has images, you may package the json and images as a single flattened zip file in assets.
        /// <see cref="FromZipStreamAsync(Device, ZipArchive, string, CancellationToken)"/>
        /// </summary>
        /// <param name="device"></param>
        /// <param name="fileName"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<LottieResult<LottieComposition>> FromAsset(RenderTarget renderTarget, string fileName, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CacheAsync(fileName, () =>
            {
                return FromAssetSync(renderTarget, fileName);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Parse an animation from src/main/assets. It is recommended to use {@link #fromRawRes(Context, int)} instead.
        /// The asset file name will be used as a cache key so future usages won't have to parse the json again.
        /// However, if your animation has images, you may package the json and images as a single flattened zip file in assets.
        /// <see cref="FromZipStreamSync(Device, ZipArchive, string)"/>
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public static LottieResult<LottieComposition> FromAssetSync(RenderTarget renderTarget, string fileName)
        {
            try
            {
                string cacheKey = "asset_" + fileName;
                if (fileName.EndsWith(".zip"))
                {
                    return FromZipStreamSync(renderTarget, new Ionic.Zip.ZipFile(fileName), cacheKey);
                }
                return FromJsonInputStreamSync(File.OpenRead(fileName), cacheKey);
            }
            catch (IOException e)
            {
                return new LottieResult<LottieComposition>(e);
            }
        }

        /// <summary>
        /// Auto-closes the stream.
        /// <see cref="FromJsonInputStreamSync(Stream, bool"/>
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<LottieResult<LottieComposition>> FromJsonInputStreamAsync(Stream stream, string cacheKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CacheAsync(cacheKey, () =>
            {
                return FromJsonInputStreamSync(stream, cacheKey);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Return a LottieComposition for the given InputStream to json.
        /// </summary>
        public static LottieResult<LottieComposition> FromJsonInputStreamSync(Stream stream, string cacheKey)
        {
            return FromJsonInputStreamSync(stream, cacheKey, true);
        }

        private static LottieResult<LottieComposition> FromJsonInputStreamSync(Stream stream, string cacheKey, bool close)
        {
            try
            {
                return FromJsonReaderSync(new JsonReader(new StreamReader(stream, Encoding.UTF8)), cacheKey);
            }
            finally
            {
                if (close)
                {
                    stream.CloseQuietly();
                }
            }
        }

        /// <summary>
        /// <see cref="FromJsonStringSync(string, string)"/>
        /// </summary>
        /// <param name="json"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<LottieResult<LottieComposition>> FromJsonString(string json, string cacheKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CacheAsync(cacheKey, () =>
            {
                return FromJsonStringSync(json, cacheKey);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Return a LottieComposition for the specified raw json string. 
        /// If loading from a file, it is preferable to use the InputStream.
        /// </summary>
        /// <param name="json"></param>
        /// <returns></returns>
        public static LottieResult<LottieComposition> FromJsonStringSync(string json, string cacheKey)
        {
            return FromJsonReaderSync(new JsonReader(new StringReader(json)), cacheKey);
        }

        public static async Task<LottieResult<LottieComposition>> FromJsonReader(JsonReader reader, string cacheKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CacheAsync(cacheKey, () =>
            {
                return FromJsonReaderSync(reader, cacheKey);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Return a LottieComposition for the specified json. 
        /// </summary>
        /// <param name="reader"></param>
        /// <returns></returns>
        public static LottieResult<LottieComposition> FromJsonReaderSync(JsonReader reader, string cacheKey)
        {
            try
            {
                LottieComposition composition = LottieCompositionParser.Parse(reader);
                LottieCompositionCache.Instance.Put(cacheKey, composition);
                return new LottieResult<LottieComposition>(composition);
            }
            catch (Exception e)
            {
                return new LottieResult<LottieComposition>(e);
            }
        }

        public static async Task<LottieResult<LottieComposition>> FromZipStreamAsync(RenderTarget renderTarget, Ionic.Zip.ZipFile inputStream, string cacheKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CacheAsync(cacheKey, () =>
            {
                return FromZipStreamSync(renderTarget, inputStream, cacheKey);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Parses a zip input stream into a Lottie composition. 
        /// Your zip file should just be a folder with your json file and images zipped together.
        /// It will automatically store and configure any images inside the animation if they exist. 
        /// </summary>
        /// <param name="device"></param>
        /// <param name="inputStream"></param>
        /// <param name="cacheKey"></param>
        /// <returns></returns>
        public static LottieResult<LottieComposition> FromZipStreamSync(RenderTarget renderTarget, Ionic.Zip.ZipFile inputStream, string cacheKey)
        {
            try
            {
                return FromZipStreamSyncInternal(renderTarget, inputStream, cacheKey);
            }
            finally
            {
                inputStream.CloseQuietly();
            }
        }

        private static LottieResult<LottieComposition> FromZipStreamSyncInternal(RenderTarget renderTarget, Ionic.Zip.ZipFile inputStream, string cacheKey)
        {
            LottieComposition composition = null;
            Dictionary<string, SharpDX.Direct2D1.Bitmap> images = new Dictionary<string, SharpDX.Direct2D1.Bitmap>();

            try
            {
                foreach (Ionic.Zip.ZipEntry entry in inputStream.Entries)
                {
                    if (entry.FileName.Contains("__MACOSX"))
                    {
                        continue;
                    }
                    else if (entry.FileName.Contains(".json"))
                    {
                        using var reader = entry.OpenReader();
                        composition = FromJsonInputStreamSync(reader, cacheKey, false).Value;
                    }
                    else if (entry.FileName.Contains(".png"))
                    {
                        string[] splitName = entry.FileName.Split('/');
                        string name = splitName[splitName.Length - 1];

                        using (var decoder = new SharpDX.WIC.BitmapDecoder(imagingFactory, entry.InputStream, DecodeOptions.CacheOnDemand))
                        {
                            var bitmap = SharpDX.Direct2D1.Bitmap.FromWicBitmap(renderTarget, decoder.Preview);

                            images[name] = bitmap;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }
            }
            catch (IOException e)
            {
                return new LottieResult<LottieComposition>(e);
            }

            if (composition == null)
            {
                return new LottieResult<LottieComposition>(new ArgumentException("Unable to parse composition"));
            }

            foreach (var e in images)
            {
                LottieImageAsset imageAsset = FindImageAssetForFileName(composition, e.Key);
                if (imageAsset != null)
                {
                    imageAsset.Bitmap = e.Value;
                }
            }

            // Ensure that all bitmaps have been set. 
            foreach (var entry in composition.Images)
            {
                if (entry.Value.Bitmap == null)
                {
                    return new LottieResult<LottieComposition>(new ArgumentException("There is no image for " + entry.Value.FileName));
                }
            }

            LottieCompositionCache.Instance.Put(cacheKey, composition);
            return new LottieResult<LottieComposition>(composition);
        }

        private static LottieImageAsset FindImageAssetForFileName(LottieComposition composition, string fileName)
        {
            foreach (var asset in composition.Images.Values)
            {
                if (asset.FileName.Equals(fileName))
                {
                    return asset;
                }
            }
            return null;
        }

        /// <summary>
        /// First, check to see if there are any in-progress tasks associated with the cache key and return it if there is. 
        /// If not, create a new task for the callable. 
        /// Then, add the new task to the task cache and set up listeners to it gets cleared when done. 
        /// </summary>
        private static async Task<LottieResult<LottieComposition>> CacheAsync(string cacheKey, Func<LottieResult<LottieComposition>> callable, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_taskCache.ContainsKey(cacheKey))
            {
                return _taskCache[cacheKey].Result;
            }

            var task = Task.Run(callable, cancellationToken);

            try
            {
                _taskCache[cacheKey] = task;
                task.Wait(cancellationToken);
                _taskCache.Remove(cacheKey);
            }
            catch
            {
                _taskCache.Remove(cacheKey);
            }

            return await task;
        }
    }
}
