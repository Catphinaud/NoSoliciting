using NoSoliciting.Resources;

namespace NoSoliciting.Ml {
    public enum MlFilterStatus {
        Uninitialised,
        Preparing,
        DownloadingManifest,
        DownloadingModel,
        Initialising,
        Initialised,
        Waiting,
    }

    public static class MlFilterStatusExt {
        public static string Description(this MlFilterStatus status) {
            return status switch {
                MlFilterStatus.Uninitialised => Language.ModelStatusUninitialised,
                MlFilterStatus.Preparing => Language.ModelStatusPreparing,
                MlFilterStatus.DownloadingManifest => Language.ModelStatusDownloadingManifest,
                MlFilterStatus.DownloadingModel => Language.ModelStatusDownloadingModel,
                MlFilterStatus.Initialising => Language.ModelStatusInitialising,
                MlFilterStatus.Initialised => Language.ModelStatusInitialised,
                MlFilterStatus.Waiting => Language.ModelStatusWaiting,
                _ => status.ToString(),
            };
        }
    }
}
