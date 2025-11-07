using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NoSoliciting.Interface;
using NoSoliciting.Resources;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Newtonsoft.Json.Linq;

namespace NoSoliciting.Ml {
    public class MlFilter : IDisposable {
        public static string? LastError { get; private set; }

        private const string ManifestName = "manifest.yaml";
        private const string ModelName = "model.zip";

        public uint Version { get; }
        public Uri ReportUrl { get; }

        private IClassifier Classifier { get; }

        private MlFilter(uint version, Uri reportUrl, IClassifier classifier) {
            this.Classifier = classifier;
            this.Version = version;
            this.ReportUrl = reportUrl;
        }

        public (MessageCategory category, float confidence) ClassifyMessage(ushort channel, string message) {
            var prediction = this.Classifier.Classify(channel, message);
            var category = MessageCategoryExt.FromString(prediction.Category);

            if (category != null) {
                return ((MessageCategory) category, prediction.Confidence);
            }

            Plugin.Log.Warning($"Unknown message category: {prediction.Category}");
            return (MessageCategory.Normal, prediction.Confidence);
        }

        public static async Task<MlFilter?> Load(Plugin plugin, bool showWindow) {
            // Local override: if user provided a local model zip, attempt to load it directly
            var localOverride = plugin.Config.LocalModelZipPath;
            if (!string.IsNullOrWhiteSpace(localOverride) && File.Exists(localOverride)) {
                try {
                    plugin.MlStatus = MlFilterStatus.Initialising;
                    Plugin.Log.Info("[ML] Using local model override: {0}", localOverride);
                    var data = await File.ReadAllBytesAsync(localOverride);
                    Plugin.Log.Info("[ML] Local model size: {0} bytes; SHA256: {1}", data.Length, ToHexString(SHA256.HashData(data)));
                    var classifier = new Classifier();
                    classifier.Initialise(data);
                    var dummyManifest = new Manifest {
                        Version = 0,
                        ModelUrl = new Uri("file://" + localOverride.Replace('\\', '/')),
                        ModelHash = Convert.ToBase64String(SHA256.HashData(data)),
                        ReportUrl = new Uri("https://example.invalid")
                    };
                    plugin.MlStatus = MlFilterStatus.Initialised;
                    Plugin.Log.Info("[ML] Local model override initialised.");
                    return new MlFilter(dummyManifest.Version, dummyManifest.ReportUrl, classifier);
                } catch (Exception ex) {
                    Plugin.Log.Error(ex, "[ML] Failed to load local model override");
                    LastError = ex.Message;
                    // fall through to normal logic
                }
            }

            // Wait for configuration to specify a URL or GitHub repo if neither is set
            if (!plugin.Config.UseGithubReleases && string.IsNullOrWhiteSpace(plugin.Config.ModelManifestUrl)) {
                plugin.MlStatus = MlFilterStatus.Waiting;
                LastError = "No model manifest source configured.";
                Plugin.Log.Warning("[ML] Waiting for model manifest source configuration.");
                return null;
            }

            plugin.MlStatus = MlFilterStatus.DownloadingManifest;

            // download and parse the remote manifest
            (Manifest manifest, string source)? remote = await DownloadManifest(plugin);
            if (remote == null) {
                Plugin.Log.Warning("[ML] Could not download remote manifest. Will attempt to use cached manifest.");
            } else {
                Plugin.Log.Info("[ML] Downloaded manifest version {0}", remote.Value.manifest.Version);
            }

            // Try to load cached manifest as fallback
            var cachedManifest = LoadCachedManifest(plugin);
            Manifest? effectiveManifest = remote?.manifest ?? cachedManifest;

            if (effectiveManifest == null) {
                Plugin.Log.Error("[ML] No manifest available (remote and cached missing). Aborting.");
                plugin.MlStatus = MlFilterStatus.Uninitialised;
                LastError = "Manifest not available.";
                return null;
            }

            if (remote != null) {
                Plugin.Log.Info("[ML] Using REMOTE manifest (preferred even if version equals cache). Model URL: {0}", effectiveManifest.ModelUrl);
            } else {
                Plugin.Log.Info("[ML] Using CACHED manifest. Model URL: {0}", effectiveManifest.ModelUrl);
            }

            // If we got a remote manifest, update cache file with its source
            if (remote != null) {
                UpdateCachedFile(plugin, ManifestName, Encoding.UTF8.GetBytes(remote.Value.source));
            }

            // Try to reuse cached model if it matches the manifest's hash
            byte[]? modelData = null;
            var cachePath = CachedFilePath(plugin, ModelName);
            if (File.Exists(cachePath)) {
                try {
                    var cachedBytes = await File.ReadAllBytesAsync(cachePath);
                    var cachedHash = SHA256.HashData(cachedBytes);
                    var correctHash = effectiveManifest.Hash();
                    if (cachedHash.SequenceEqual(correctHash)) {
                        modelData = cachedBytes;
                        Plugin.Log.Info("[ML] Reusing cached model: {0} (size {1} bytes); SHA256 matches manifest.", cachePath, cachedBytes.Length);
                    } else {
                        Plugin.Log.Warning("[ML] Cached model hash mismatch; will download fresh. Cached SHA256={0}", ToHexString(cachedHash));
                    }
                } catch (Exception ex) {
                    Plugin.Log.Warning(ex, "[ML] Failed to read cached model; will download fresh.");
                }
            } else {
                Plugin.Log.Info("[ML] No cached model found at {0}; will download fresh.", cachePath);
            }

            // Download model if necessary
            if (modelData == null) {
                plugin.MlStatus = MlFilterStatus.DownloadingModel;
                Plugin.Log.Info("[ML] Downloading model from {0}", effectiveManifest.ModelUrl);
                modelData = await DownloadModel(effectiveManifest.ModelUrl);
            }

            // give up if we couldn't get any data at this point
            if (modelData == null) {
                Plugin.Log.Warning("[ML] Could not download model.");
                plugin.MlStatus = MlFilterStatus.Uninitialised;
                return null;
            }

            // validate checksum (retry up to 3 times only if downloading; if came from cache we already validated)
            var correct = effectiveManifest.Hash();
            var hashNow = SHA256.HashData(modelData);
            if (!hashNow.SequenceEqual(correct)) {
                var retries = 0;
                const int maxRetries = 3;
                while (!hashNow.SequenceEqual(correct) && retries < maxRetries) {
                    retries++;
                    Plugin.Log.Warning("[ML] Model checksum mismatch (attempt {0}/{1}); redownloading...", retries, maxRetries);
                    modelData = await DownloadModel(effectiveManifest.ModelUrl);
                    if (modelData == null) break;
                    hashNow = SHA256.HashData(modelData);
                }
            }

            if (modelData == null || !SHA256.HashData(modelData).SequenceEqual(effectiveManifest.Hash())) {
                Plugin.Log.Error("[ML] Model checksum still invalid after retries. Aborting.");
                LastError = "Checksum mismatch.";
                plugin.MlStatus = MlFilterStatus.Uninitialised;
                return null;
            }

            plugin.MlStatus = MlFilterStatus.Initialising;
            Plugin.Log.Info("[ML] Model downloaded/validated. Size={0} bytes; SHA256={1}", modelData.Length, ToHexString(SHA256.HashData(modelData)));

            // Save model to cache (even if from cache we can re-save to ensure freshness)
            UpdateCachedFile(plugin, ModelName, modelData);

            // initialise the classifier
            var classifier2 = new Classifier();
            classifier2.Initialise(modelData);
            Plugin.Log.Info("[ML] Classifier initialised.");

            return new MlFilter(
                effectiveManifest.Version,
                effectiveManifest.ReportUrl,
                classifier2
            );
        }

        private static async Task<byte[]?> DownloadModel(Uri url) {
            try {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NoSoliciting-Plugin/1.0");
                Plugin.Log.Info("[ML] HTTP GET: {0}", url);
                var data = await client.GetByteArrayAsync(url);
                Plugin.Log.Info("[ML] Downloaded {0} bytes from model URL.", data.Length);
                return data;
            } catch (Exception e) {
                Plugin.Log.Error("[ML] Could not download newest model.");
                Plugin.Log.Error(e.ToString());
                LastError = e.Message;
                return null;
            }
        }

        private static string CachedFilePath(Plugin plugin, string name) {
            var pluginFolder = plugin.Interface.ConfigDirectory.ToString();
            Directory.CreateDirectory(pluginFolder);
            return Path.Combine(pluginFolder, name);
        }

        private static async void UpdateCachedFile(Plugin plugin, string name, byte[] data) {
            try {
                var cachePath = CachedFilePath(plugin, name);
                using var file = File.Create(cachePath);
                await file.WriteAsync(data, 0, data.Length);
                await file.FlushAsync();
                Plugin.Log.Info("[ML] Saved cache file: {0} ({1} bytes)", cachePath, data.Length);
            } catch (Exception ex) {
                Plugin.Log.Warning(ex, "[ML] Failed to save cache file {0}", name);
            }
        }

        private static async Task<(Manifest manifest, string source)?> DownloadManifest(Plugin plugin) {
            try {
                if (plugin.Config.UseGithubReleases) {
                    Plugin.Log.Info("[ML] Fetching manifest from GitHub releases. Repo={0}, Tag={1}, Asset={2}",
                        plugin.Config.GithubRepo, plugin.Config.GithubReleaseTag ?? "latest", plugin.Config.GithubManifestAssetName);
                    var manifestText = await DownloadManifestFromGithub(plugin);
                    if (manifestText == null) {
                        return null;
                    }
                    LastError = null;
                    return (LoadYaml<Manifest>(manifestText), manifestText);
                }

                var urlString = plugin.Config.ModelManifestUrl;
                if (string.IsNullOrWhiteSpace(urlString)) {
                    return null;
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NoSoliciting-Plugin/1.0");
                Plugin.Log.Info("[ML] Fetching manifest from URL: {0}", urlString);
                var data = await client.GetStringAsync(urlString);
                LastError = null;
                return (LoadYaml<Manifest>(data), data);
            } catch (Exception e) when (e is WebException or YamlException or HttpRequestException) {
                Plugin.Log.Error("[ML] Could not download newest model manifest.");
                Plugin.Log.Error(e.ToString());
                LastError = e.Message;
                return null;
            }
        }

        private static async Task<string?> DownloadManifestFromGithub(Plugin plugin) {
            try {
                var repo = plugin.Config.GithubRepo;
                if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/')) {
                    LastError = "Invalid GitHub repo format.";
                    Plugin.Log.Error("[ML] Invalid GitHub repo format: {0}", repo);
                    return null;
                }

                var tag = plugin.Config.GithubReleaseTag;
                string apiUrl = string.IsNullOrWhiteSpace(tag)
                    ? $"https://api.github.com/repos/{repo}/releases/latest"
                    : $"https://api.github.com/repos/{repo}/releases/tags/{tag}";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NoSoliciting-Plugin/1.0");
                Plugin.Log.Info("[ML] GitHub API: {0}", apiUrl);
                var json = await client.GetStringAsync(apiUrl);
                var release = JObject.Parse(json);
                var assets = release["assets"] as JArray;
                if (assets == null) {
                    LastError = "No assets in release.";
                    Plugin.Log.Error("[ML] No assets in GitHub release response.");
                    return null;
                }

                var manifestAssetName = plugin.Config.GithubManifestAssetName;

                var manifestAsset = assets.FirstOrDefault(a => string.Equals((string?) a["name"], manifestAssetName, StringComparison.OrdinalIgnoreCase));
                if (manifestAsset == null) {
                    LastError = $"Asset '{manifestAssetName}' not found.";
                    Plugin.Log.Error("[ML] Manifest asset not found in release: {0}", manifestAssetName);
                    return null;
                }

                var manifestDownloadUrl = (string?) manifestAsset["browser_download_url"];
                if (string.IsNullOrWhiteSpace(manifestDownloadUrl)) {
                    LastError = "Missing manifest asset download URL.";
                    Plugin.Log.Error("[ML] Manifest asset missing browser_download_url.");
                    return null;
                }

                Plugin.Log.Info("[ML] Downloading manifest asset from: {0}", manifestDownloadUrl);
                var manifestText = await client.GetStringAsync(manifestDownloadUrl);

                // Return the manifest as-is; do not override ModelUrl. This is tolerant to GitHub model asset issues.
                return manifestText;
            } catch (Exception e) {
                Plugin.Log.Error(e, "[ML] Error while downloading manifest from GitHub");
                LastError = e.Message;
                return null;
            }
        }

        private static Manifest? LoadCachedManifest(Plugin plugin) {
            var manifestPath = CachedFilePath(plugin, ManifestName);
            if (!File.Exists(manifestPath)) {
                return null;
            }

            string data;
            try {
                data = File.ReadAllText(manifestPath);
                Plugin.Log.Info("[ML] Loaded cached manifest from {0}", manifestPath);
            } catch (IOException ex) {
                Plugin.Log.Warning(ex, "[ML] Failed to read cached manifest at {0}", manifestPath);
                return null;
            }

            try {
                return LoadYaml<Manifest>(data);
            } catch (YamlException ex) {
                Plugin.Log.Warning(ex, "[ML] Failed to parse cached manifest YAML.");
                return null;
            }
        }

        private static T LoadYaml<T>(string data) {
            var de = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return de.Deserialize<T>(data);
        }

        private static string ToHexString(byte[] bytes) {
            var c = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0, j = 0; i < bytes.Length; i++) {
                var b = bytes[i];
                c[j++] = hex[b >> 4];
                c[j++] = hex[b & 0xF];
            }
            return new string(c);
        }

        public void Dispose() {
            this.Classifier.Dispose();
        }
    }
}
