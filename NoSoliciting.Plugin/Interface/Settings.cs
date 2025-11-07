using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using NoSoliciting.Ml;
using NoSoliciting.Resources;

namespace NoSoliciting.Interface {
    public class Settings : IDisposable {
        private Plugin Plugin { get; }
        private PluginUi Ui { get; }

        private bool _isOpen;
        private SettingsPage _page = SettingsPage.Overview;

        private string _testMessage = string.Empty;
        private ChatType _testChatType = ChatType.Say;
        private MessageCategory? _testCategory;
        private float _testConfidence;
        private bool _testWouldFilter;
        private bool _testRan;

        private readonly (string label, ChatType chatType, string text)[] _examples = {
            ("RMT Gil", ChatType.Shout, "Cheap gil for sale! 10M=5$ bestgil.com"),
            ("RMT Content", ChatType.Shout, "Offering raid clears & leveling services, fast delivery discord"),
            ("Phishing", ChatType.Say, "FREE GIFT claim at mogstation-freerewards.com now"),
            ("Trade", ChatType.Shout, "WTS rare mount PST offers"),
            ("Free Company", ChatType.Shout, "FC recruiting casual players, friendly helpful community!"),
            ("Roleplaying", ChatType.Yell, "Tavern RP tonight in Limsa – seeking adventurers"),
            ("Static", ChatType.Shout, "Static recruiting WAR + WHM weekday prog 8pm EST"),
            ("Community", ChatType.Shout, "Join our discord for giveaways & events!"),
            ("Fluff", ChatType.Say, "Good morning Eorzea!"),
        };

        public Settings(Plugin plugin, PluginUi ui)
        {
            this.Plugin = plugin;
            this.Ui = ui;

            this.Plugin.Interface.UiBuilder.OpenConfigUi += this.Open;
        }

        public void Dispose()
        {
            this.Plugin.Interface.UiBuilder.OpenConfigUi -= this.Open;
        }

        private void Open()
        {
            this._isOpen = true;
        }

        public void Toggle()
        {
            this._isOpen = !this._isOpen;
        }

        public void Show()
        {
            this._isOpen = true;
        }

        public void Draw()
        {
            if (!this._isOpen) return;

            var windowTitle = string.Format(Language.Settings, Plugin.Name);
            var open = this._isOpen;
            if (!ImGui.Begin($"{windowTitle}###NoSoliciting settings", ref open, ImGuiWindowFlags.NoCollapse)) {
                this._isOpen = open;
                ImGui.End();
                return;
            }

            this._isOpen = open;

            // Layout: Sidebar (left) + Content (right)
            var sidebarWidth = 180f;
            ImGui.BeginChild("##ns-sidebar", new Vector2(sidebarWidth, 0), true);
            DrawSidebar();
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##ns-content", new Vector2(0, 0), false);
            switch (this._page) {
                case SettingsPage.Overview:
                    DrawOverview();
                    break;
                case SettingsPage.Model:
                    DrawModel();
                    break;
                case SettingsPage.Test:
                    DrawTest();
                    break;
                case SettingsPage.Filters:
                    DrawFilters();
                    break;
                case SettingsPage.Advanced:
                    DrawAdvanced();
                    break;
                case SettingsPage.Other:
                    DrawOther();
                    break;
            }

            ImGui.EndChild();

            ImGui.End();
        }

        private enum SettingsPage { Overview, Model, Test, Filters, Advanced, Other }

        private void DrawSidebar()
        {
            ImGui.TextUnformatted("NoSoliciting");
            ImGui.Separator();

            SidebarButton("Overview", SettingsPage.Overview, "Quick status and common actions.");
            SidebarButton("Model", SettingsPage.Model, "Configure model source and reload.");
            SidebarButton("Test", SettingsPage.Test, "Try messages against the model.");
            SidebarButton("Filters", SettingsPage.Filters, "Custom chat and Party Finder filters.");
            SidebarButton("Advanced", SettingsPage.Advanced, "Per-category chat-type controls.");
            SidebarButton("Other", SettingsPage.Other, "Language and logging options.");
        }

        private void SidebarButton(string label, SettingsPage page, string? hint = null)
        {
            var selected = this._page == page;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.9f, 0.6f));
            if (ImGui.Button($"{label}##sidebar-{page}", new Vector2(-1, 0))) {
                this._page = page;
            }

            if (selected) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(hint)) {
                using var _ = ImRaii.Tooltip();
                ImGui.Text(hint);
            }
        }

        private void DrawOverview()
        {
            ImGui.TextUnformatted("Overview");
            ImGui.Separator();

            ImGui.Spacing();
            ImGui.TextUnformatted("Model");
            ImGui.TextUnformatted(string.Format(Language.ModelTabVersion, this.Plugin.MlFilter?.Version));
            ImGui.TextUnformatted(string.Format(Language.ModelTabStatus, this.Plugin.MlStatus.Description()));
            var lastError = MlFilter.LastError;
            if (lastError != null) {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextWrapped(string.Format(Language.ModelTabError, lastError));
                ImGui.PopStyleColor();
            }

            if (ImGui.Button(Language.UpdateModel)) {
                if (ImGui.GetIO().KeyCtrl || this.Plugin.MlStatus is MlFilterStatus.Uninitialised or MlFilterStatus.Initialised) {
                    this.Plugin.MlFilter?.Dispose();
                    this.Plugin.MlFilter = null;
                    this.Plugin.MlStatus = MlFilterStatus.Uninitialised;
                    this.Plugin.InitialiseMachineLearning(ImGui.GetIO().KeyAlt);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Open model folder")) {
                try {
                    var folder = this.Plugin.Interface.ConfigDirectory.FullName;
                    System.Diagnostics.Process.Start("explorer.exe", folder);
                } catch {
                    /* ignore */
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted("Basic categories");

            foreach (var category in MessageCategoryExt.UiOrder) {
                var check = this.Plugin.Config.BasicMlFilters.Contains(category);
                if (ImGui.Checkbox(category.Name(), ref check)) {
                    if (check) this.Plugin.Config.BasicMlFilters.Add(category);
                    else this.Plugin.Config.BasicMlFilters.Remove(category);
                    this.Plugin.Config.Save();
                }

                if (ImGui.IsItemHovered()) {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24);
                    ImGui.TextUnformatted(category.Description());
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
            }

            // Test Mode
            var testMode = this.Plugin.Config.TestMode;
            if (ImGui.Checkbox("Test Mode (do not hide, just log)", ref testMode)) {
                this.Plugin.Config.TestMode = testMode;
                this.Plugin.Config.Save();
            }

            // Confidence Threshold
            var threshold = this.Plugin.Config.ConfidenceThreshold;
            ImGui.TextUnformatted("Confidence threshold");
            if (ImGui.SliderFloat("##ns-threshold", ref threshold, 0.0f, 1.0f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp)) {
                this.Plugin.Config.ConfidenceThreshold = threshold;
                this.Plugin.Config.Save();
            }
        }

        private void DrawModel()
        {
            ImGui.TextUnformatted("Model Source");
            ImGui.Separator();

            // Local model override
            var localZip = this.Plugin.Config.LocalModelZipPath ?? string.Empty;
            ImGui.TextUnformatted("Local model override (.zip)");
            if (ImGui.InputText("##ml-local-zip", ref localZip, 400)) {
                this.Plugin.Config.LocalModelZipPath = string.IsNullOrWhiteSpace(localZip) ? null : localZip;
            }

            ImGui.SameLine();
            if (ImGui.Button("Browse...##ml-browse-local-zip")) {
                var folder = this.Plugin.Interface.ConfigDirectory.FullName;
                try { System.Diagnostics.Process.Start("explorer.exe", folder); } catch {
                    /* ignore */
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear##ml-clear-local-zip")) {
                this.Plugin.Config.LocalModelZipPath = null;
            }

            ImGui.Spacing();
            var useGithub = this.Plugin.Config.UseGithubReleases;
            if (ImGui.Checkbox("Use GitHub Releases###ml-use-github", ref useGithub)) {
                this.Plugin.Config.UseGithubReleases = useGithub;
                this.Plugin.Config.Save();
            }

            if (useGithub) {
                var repo = this.Plugin.Config.GithubRepo;
                if (ImGui.InputText("GitHub Repo (owner/name)###ml-github-repo", ref repo, 200)) {
                    this.Plugin.Config.GithubRepo = repo;
                }

                Tooltip("Example: Catphinaud/NoSoliciting-Model");

                var assetName = this.Plugin.Config.GithubManifestAssetName;
                if (ImGui.InputText("Manifest Asset Name###ml-github-asset", ref assetName, 200)) {
                    this.Plugin.Config.GithubManifestAssetName = assetName;
                }

                Tooltip("Default: manifest.yaml");

                var tag = this.Plugin.Config.GithubReleaseTag ?? string.Empty;
                if (ImGui.InputText("Release Tag (optional)###ml-github-tag", ref tag, 200)) {
                    this.Plugin.Config.GithubReleaseTag = string.IsNullOrWhiteSpace(tag) ? null : tag;
                }
            } else {
                var manifestUrl = this.Plugin.Config.ModelManifestUrl ?? string.Empty;
                if (ImGui.InputText("Manifest URL###ml-manifest-url", ref manifestUrl, 500)) {
                    this.Plugin.Config.ModelManifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? null : manifestUrl;
                }
            }

            ImGui.Spacing();
            if (ImGui.Button("Save & Reload Model###ml-save-reload")) {
                this.Plugin.Config.Save();
                this.Plugin.MlFilter?.Dispose();
                this.Plugin.MlFilter = null;
                this.Plugin.MlStatus = MlFilterStatus.Uninitialised;
                this.Plugin.InitialiseMachineLearning(false);
            }
        }

        private void DrawFilters()
        {
            ImGui.TextUnformatted("Filters");
            ImGui.Separator();

            if (ImGui.CollapsingHeader(Language.ChatFilters)) {
                var customChat = this.Plugin.Config.CustomChatFilter;
                if (ImGui.Checkbox(Language.EnableCustomChatFilters, ref customChat)) {
                    this.Plugin.Config.CustomChatFilter = customChat;
                    this.Plugin.Config.Save();
                }

                if (this.Plugin.Config.CustomChatFilter) {
                    var substrings = this.Plugin.Config.ChatSubstrings;
                    var regexes = this.Plugin.Config.ChatRegexes;
                    DrawCustomList("chat", ref substrings, ref regexes);
                }
            }

            if (ImGui.CollapsingHeader(Language.PartyFinderFilters)) {
                var filterHugeItemLevelPFs = this.Plugin.Config.FilterHugeItemLevelPFs;
                if (ImGui.Checkbox(Language.FilterIlvlPfs, ref filterHugeItemLevelPFs)) {
                    this.Plugin.Config.FilterHugeItemLevelPFs = filterHugeItemLevelPFs;
                    this.Plugin.Config.Save();
                }

                var considerPrivate = this.Plugin.Config.ConsiderPrivatePfs;
                if (ImGui.Checkbox(Language.FilterPrivatePfs, ref considerPrivate)) {
                    this.Plugin.Config.ConsiderPrivatePfs = considerPrivate;
                    this.Plugin.Config.Save();
                }

                var customPf = this.Plugin.Config.CustomPFFilter;
                if (ImGui.Checkbox(Language.EnableCustomPartyFinderFilters, ref customPf)) {
                    this.Plugin.Config.CustomPFFilter = customPf;
                    this.Plugin.Config.Save();
                }

                if (this.Plugin.Config.CustomPFFilter) {
                    var substrings = this.Plugin.Config.PFSubstrings;
                    var regexes = this.Plugin.Config.PFRegexes;
                    DrawCustomList("pf", ref substrings, ref regexes);
                }
            }
        }

        private void DrawAdvanced()
        {
            ImGui.TextUnformatted("Advanced");
            ImGui.Separator();

            foreach (var category in MessageCategoryExt.UiOrder) {
                if (!ImGui.CollapsingHeader(category.Name())) continue;

                if (!this.Plugin.Config.MlFilters.ContainsKey(category)) {
                    this.Plugin.Config.MlFilters[category] = new HashSet<ChatType>();
                }

                var types = this.Plugin.Config.MlFilters[category];

                void DrawType(ChatType type, string id)
                {
                    var name = type.Name(this.Plugin.DataManager);
                    var check = types.Contains(type);
                    if (!ImGui.Checkbox($"{name}##{id}", ref check)) return;
                    if (check) types.Add(type);
                    else types.Remove(type);
                    this.Plugin.Config.Save();
                }

                DrawType(ChatType.None, category.ToString());
                foreach (var type in Filter.FilteredChatTypes) DrawType(type, category.ToString());
            }
        }

        private void DrawOther()
        {
            ImGui.TextUnformatted("Other");
            ImGui.Separator();

            var useGameLanguage = this.Plugin.Config.FollowGameLanguage;
            if (ImGui.Checkbox(Language.OtherGameLanguage, ref useGameLanguage)) {
                this.Plugin.Config.FollowGameLanguage = useGameLanguage;
                this.Plugin.Config.Save();
                this.Plugin.ConfigureLanguage();
            }

            var logFilteredPfs = this.Plugin.Config.LogFilteredPfs;
            if (ImGui.Checkbox(Language.LogFilteredPfs, ref logFilteredPfs)) {
                this.Plugin.Config.LogFilteredPfs = logFilteredPfs;
                this.Plugin.Config.Save();
            }

            var logFilteredMessages = this.Plugin.Config.LogFilteredChat;
            if (ImGui.Checkbox(Language.LogFilteredMessages, ref logFilteredMessages)) {
                this.Plugin.Config.LogFilteredChat = logFilteredMessages;
                this.Plugin.Config.Save();
            }
        }

        private void Tooltip(string text)
        {
            if (!ImGui.IsItemHovered()) return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        private void DrawCustomList(string name, ref List<string> substrings, ref List<string> regexes)
        {
            ImGui.Columns(2);

            ImGui.TextUnformatted(Language.SubstringsToFilter);
            if (ImGui.BeginChild($"##{name}-substrings", new Vector2(0, 175))) {
                for (var i = 0; i < substrings.Count; i++) {
                    var input = substrings[i];
                    if (ImGui.InputText($"##{name}-substring-{i}", ref input, 1_000)) {
                        substrings[i] = input;
                    }

                    ImGui.SameLine();
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##{name}-substring-{i}-remove")) {
                        substrings.RemoveAt(i);
                    }

                    ImGui.PopFont();
                }

                ImGui.PushFont(UiBuilder.IconFont);
                if (ImGui.Button($"{FontAwesomeIcon.Plus.ToIconString()}##{name}-substring-add")) {
                    substrings.Add("");
                }

                ImGui.PopFont();

                ImGui.EndChild();
            }

            ImGui.NextColumn();

            ImGui.TextUnformatted(Language.RegularExpressionsToFilter);
            if (ImGui.BeginChild($"##{name}-regexes", new Vector2(0, 175))) {
                for (var i = 0; i < regexes.Count; i++) {
                    var input = regexes[i];
                    if (ImGui.InputText($"##{name}-regex-{i}", ref input, 1_000)) {
                        try {
                            _ = new Regex(input);
                            regexes[i] = input; // only update if valid
                        } catch (ArgumentException) {
                            // ignore invalid regex while typing
                        }
                    }

                    ImGui.SameLine();
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##{name}-regex-{i}-remove")) {
                        regexes.RemoveAt(i);
                    }

                    ImGui.PopFont();
                }

                ImGui.PushFont(UiBuilder.IconFont);
                if (ImGui.Button($"{FontAwesomeIcon.Plus.ToIconString()}##{name}-regex-add")) {
                    regexes.Add("");
                }

                ImGui.PopFont();

                ImGui.EndChild();
            }

            ImGui.Columns(1);

            var saveLoc = Language.SaveFilters;
            if (ImGui.Button($"{saveLoc}##{name}-save")) {
                this.Plugin.Config.Save();
                this.Plugin.Config.CompileRegexes();
            }
        }

        private void DrawTest() {
            ImGui.TextUnformatted("Test Classification");
            ImGui.Separator();

            if (this.Plugin.MlFilter == null) {
                ImGui.TextWrapped("Model not loaded. Load or configure the model on the Model page first.");
                return;
            }

            // Chat type selector
            if (ImGui.BeginCombo("Chat Type", _testChatType.Name(this.Plugin.DataManager))) {
                foreach (var ct in Filter.FilteredChatTypes) {
                    var selected = ct == _testChatType;
                    if (ImGui.Selectable(ct.Name(this.Plugin.DataManager), selected)) {
                        _testChatType = ct;
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                if (ImGui.Selectable(ChatType.None.Name(this.Plugin.DataManager), _testChatType == ChatType.None)) {
                    _testChatType = ChatType.None;
                }
                ImGui.EndCombo();
            }

            ImGui.TextUnformatted("Message (multi-line)");
            ImGui.InputTextMultiline("##ns-test-msg", ref _testMessage, 4000, new Vector2(-1, 150));

            if (ImGui.Button("Classify###ns-test-classify")) {
                ClassifyTestMessage();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear###ns-test-clear")) {
                _testMessage = string.Empty; _testRan = false; _testCategory = null; _testConfidence = 0; _testWouldFilter = false;
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Examples (click to load):");
            ImGui.BeginChild("##ns-test-examples", new Vector2(-1, 120), true);
            foreach (var ex in _examples) {
                if (ImGui.SmallButton(ex.label + "##exbtn")) {
                    _testMessage = ex.text;
                    _testChatType = ex.chatType;
                    _testRan = false; // require explicit classify
                }
                if (ImGui.IsItemHovered()) {
                    ImGui.BeginTooltip();
                    ImGui.Text(ex.text);
                    ImGui.EndTooltip();
                }
            }
            ImGui.EndChild();

            ImGui.Spacing();
            ImGui.Separator();

            if (_testRan) {
                if (_testCategory == null) {
                    ImGui.TextWrapped("Classification: Normal / Not filtered");
                } else {
                    ImGui.TextWrapped($"Classification: {_testCategory.Value.Name()} ({_testConfidence:P2})");
                }
                ImGui.TextWrapped($"Confidence Threshold: {this.Plugin.Config.ConfidenceThreshold:P0}");
                ImGui.TextWrapped($"Would Filter (normal mode): {(_testWouldFilter ? "Yes" : "No")}");
                if (this.Plugin.Config.TestMode && _testWouldFilter) {
                    ImGui.TextWrapped("Current Test Mode active: message would NOT be hidden.");
                }
            } else {
                ImGui.TextDisabled("No classification run yet.");
            }
        }

        private void ClassifyTestMessage() {
            _testRan = true;
            _testCategory = null; _testConfidence = 0; _testWouldFilter = false;
            var text = _testMessage?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (this.Plugin.MlFilter == null) return;

            var (cat, conf) = this.Plugin.MlFilter.ClassifyMessage((ushort)_testChatType, text);
            if (cat != MessageCategory.Normal) {
                _testCategory = cat;
                _testConfidence = conf;
                var passes = conf >= this.Plugin.Config.ConfidenceThreshold && this.Plugin.Config.MlEnabledOn(cat, _testChatType);
                _testWouldFilter = passes;
            } else {
                _testCategory = null;
                _testConfidence = conf;
                _testWouldFilter = false;
            }
        }
    }
}
