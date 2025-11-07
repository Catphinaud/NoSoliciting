using System;

namespace NoSoliciting.Interface {
    public class PluginUi : IDisposable {
        private Plugin Plugin { get; }

        public Settings Settings { get; }

        public PluginUi(Plugin plugin) {
            this.Plugin = plugin;

            this.Settings = new Settings(plugin, this);

            this.Plugin.Interface.UiBuilder.Draw += this.Draw;
        }

        public void Dispose() {
            this.Plugin.Interface.UiBuilder.Draw -= this.Draw;
            this.Settings.Dispose();
        }

        private void Draw() {
            this.Settings.Draw();
        }
    }
}
