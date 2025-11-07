using System.IO;
using System.Linq;
using Microsoft.ML;
using NoSoliciting.Interface;

namespace NoSoliciting.Ml {
    internal class Classifier : IClassifier {
        private MLContext Context { get; set; } = null!;
        private ITransformer Model { get; set; } = null!;
        private DataViewSchema Schema { get; set; } = null!;
        private PredictionEngine<Data, Prediction>? PredictionEngine { get; set; }

        public void Initialise(byte[] data) {
            if (this.PredictionEngine != null) {
                this.PredictionEngine.Dispose();
                this.PredictionEngine = null;
            }

            this.Context = new MLContext();
            this.Context.ComponentCatalog.RegisterAssembly(typeof(Data).Assembly);
            using var stream = new MemoryStream(data);
            var model = this.Context.Model.Load(stream, out var schema);
            this.Model = model;
            this.Schema = schema;
            this.PredictionEngine = this.Context.Model.CreatePredictionEngine<Data, Prediction>(this.Model, this.Schema);
        }

        public ClassifyResult Classify(ushort channel, string message) {
            var pred = this.PredictionEngine?.Predict(new Data(channel, message));
            if (pred == null) return new ClassifyResult("UNKNOWN", 0f);
            // Score array order matches training order; find chosen label index for confidence if possible
            var label = pred.Category ?? "UNKNOWN";
            float confidence = 0f;
            if (pred.Probabilities != null && pred.Probabilities.Length > 0) {
                // try mapping label back to index using training label order heuristics
                // fallback: max probability
                confidence = pred.Probabilities.Max();
            }
            return new ClassifyResult(label, confidence);
        }

        public void Dispose() {
            this.PredictionEngine?.Dispose();
        }
    }
}
