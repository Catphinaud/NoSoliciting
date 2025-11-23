using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using NoSoliciting.Interface;
using NoSoliciting.Ml;
using NoSoliciting.Resources;

namespace NoSoliciting {
    // ReSharper disable once ClassNeverInstantiated.Global
    public class Plugin : IDalamudPlugin {
        private bool _disposedValue;

        internal static string Name => "NoSoliciting";

        private Filter Filter { get; }

        [PluginService]
        internal static IPluginLog Log { get; private set; } = null!;

        [PluginService]
        internal IDalamudPluginInterface Interface { get; init; } = null!;

        [PluginService]
        internal IClientState ClientState { get; init; } = null!;

        [PluginService]
        internal IChatGui ChatGui { get; init; } = null!;

        [PluginService]
        internal IPartyFinderGui PartyFinderGui { get; init; } = null!;

        [PluginService]
        internal IDataManager DataManager { get; init; } = null!;

        [PluginService]
        internal ICommandManager CommandManager { get; init; } = null!;

        [PluginService]
        internal IToastGui ToastGui { get; init; } = null!;
        
        [PluginService]
        
        internal IGameInteropProvider GameInteropProvider { get; init; } = null!;

        internal PluginConfiguration Config { get; }
        internal PluginUi Ui { get; }
        private Commands Commands { get; }
        internal MlFilterStatus MlStatus { get; set; } = NoSoliciting.Ml.MlFilterStatus.Uninitialised;
        internal MlFilter? MlFilter { get; set; }

        private readonly List<Message> _messageHistory = new();
        internal IEnumerable<Message> MessageHistory => this._messageHistory;

        private readonly List<Message> _partyFinderHistory = new();
        internal IEnumerable<Message> PartyFinderHistory => this._partyFinderHistory;

        // ReSharper disable once MemberCanBePrivate.Global
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
        public string AssemblyLocation { get; private set; } = Assembly.GetExecutingAssembly().Location;

        // Current plugin version (assembly informational or file version)
        internal string CurrentPluginVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        public Plugin(IPluginLog log, IDalamudPluginInterface @interface, IClientState clientState, IChatGui chatGui,
            IPartyFinderGui partyFinderGui, IDataManager dataManager, ICommandManager commandManager, IToastGui toastGui,IGameInteropProvider gameInteropProvider)
        {
            Log = log;
            Interface = @interface;
            ClientState = clientState;
            ChatGui = chatGui;
            PartyFinderGui = partyFinderGui;
            DataManager = dataManager;
            CommandManager = commandManager;
            ToastGui = toastGui;
            GameInteropProvider = gameInteropProvider;

            string path = Environment.GetEnvironmentVariable("PATH")!;
            string newPath = Path.GetDirectoryName(this.AssemblyLocation)!;
            Environment.SetEnvironmentVariable("PATH", $"{path};{newPath}");

            this.Config = this.Interface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
            this.Config.Initialise(this.Interface);

            this.ConfigureLanguage();
            this.Interface.LanguageChanged += this.OnLanguageUpdate;

            this.Ui = new PluginUi(this);
            this.Commands = new Commands(this);

            this.Filter = new Filter(this);

            this.InitialiseMachineLearning(false);

            // pre-compute the max ilvl to prevent stutter
            try {
                FilterUtil.MaxItemLevelAttainable(this.DataManager);
            } catch (Exception ex) {
                Plugin.Log.Error(ex, "Exception while computing max item level");
            }
        }

        protected virtual void Dispose(bool disposing) {
            if (this._disposedValue) {
                return;
            }

            if (disposing) {
                this.Filter.Dispose();
                this.MlFilter?.Dispose();
                this.Commands.Dispose();
                this.Ui.Dispose();
                this.Interface.LanguageChanged -= this.OnLanguageUpdate;
            }

            this._disposedValue = true;
        }

        private void OnLanguageUpdate(string langCode) {
            this.ConfigureLanguage(langCode);
        }

        internal void ConfigureLanguage(string? langCode = null) {
            if (this.Config.FollowGameLanguage) {
                langCode = this.ClientState.ClientLanguage switch {
                    ClientLanguage.Japanese => "ja",
                    ClientLanguage.English => "en",
                    ClientLanguage.German => "de",
                    ClientLanguage.French => "fr",
                    _ => throw new ArgumentOutOfRangeException(nameof(this.ClientState.ClientLanguage), "Unknown ClientLanguage"),
                };
            }

            langCode ??= this.Interface.UiLanguage;
            // I don't fucking trust this. Not since last time.
            // ReSharper disable once ConstantNullCoalescingCondition
            Language.Culture = new CultureInfo(langCode ?? "en");
        }

        internal void InitialiseMachineLearning(bool showWindow) {
            if (this.MlFilter != null) {
                return;
            }

            _ = Task.Run(async () => {
                try {
                    var loaded = await MlFilter.Load(this, showWindow);
                    this.MlFilter = loaded;

                    if (loaded == null) {
                        // Preserve whatever status was set inside Load (e.g., Waiting or Uninitialised)
                        Plugin.Log.Info("Machine learning model not loaded (null). Current status: {0}", this.MlStatus);
                        return;
                    }

                    this.MlStatus = NoSoliciting.Ml.MlFilterStatus.Initialised;
                    Log.Info("Machine learning model loaded");

                    // Update persisted versions if successful
                    // and not using local override (model version 0 is override sentinel)
                    if (loaded.Version != 0) {
                        if (this.Config.LastLoadedModelVersion != loaded.Version || this.Config.LastLoadedPluginVersion != this.CurrentPluginVersion) {
                            Plugin.Log.Info("[ML] Persisting model version {0} and plugin version {1}", loaded.Version, this.CurrentPluginVersion);
                            this.Config.LastLoadedModelVersion = loaded.Version;
                            this.Config.LastLoadedPluginVersion = this.CurrentPluginVersion;
                            this.Config.Save();
                        }
                    } else {
                        Plugin.Log.Info("[ML] Local override model in use; not updating persisted version fields.");
                    }
                } catch (Exception e) {
                    this.MlStatus = NoSoliciting.Ml.MlFilterStatus.Uninitialised;
                    Plugin.Log.Error(e, "Failed to load ML filter");
                }
            });
        }

        public void AddMessageHistory(Message message) {
            this._messageHistory.Insert(0, message);

            while (this._messageHistory.Count > 250) {
                this._messageHistory.RemoveAt(this._messageHistory.Count - 1);
            }
        }

        public void ClearPartyFinderHistory() {
            this._partyFinderHistory.Clear();
        }

        public void AddPartyFinderHistory(Message message) {
            this._partyFinderHistory.Add(message);
        }

        public void Dispose() {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
