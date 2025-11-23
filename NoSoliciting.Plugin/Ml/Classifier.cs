using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using NoSoliciting.Interface;

namespace NoSoliciting.Ml
{
    internal class Classifier : IClassifier
    {
        private MLContext Context { get; set; } = null!;
        private ITransformer Model { get; set; } = null!;
        private DataViewSchema Schema { get; set; } = null!;
        private PredictionEngine<Data, Prediction>? PredictionEngine { get; set; }
        private Dictionary<string, int>? _scoreLabelIndex;

        public void Initialise(byte[] data)
        {
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

            // Build mapping from Score slot names to indices if available
            this._scoreLabelIndex = null;
            var col = this.Schema.GetColumnOrNull("Score");
            if (col.HasValue) {
                int scoreColIndex = col.Value.Index;
                var scoreCol = this.Schema[scoreColIndex];
                if (scoreCol.Type is VectorDataViewType vType && vType.ItemType == NumberDataViewType.Single) {
                    VBuffer<ReadOnlyMemory<char>> slotNames = default;
                    scoreCol.GetSlotNames(ref slotNames);

                    if (slotNames.Length == vType.Size && slotNames.Length > 0) {
                        var arr = slotNames.GetValues();
                        var dict = new Dictionary<string, int>(arr.Length);
                        for (int i = 0; i < arr.Length; i++) {
                            var name = arr[i].ToString();
                            if (!dict.ContainsKey(name)) dict[name] = i;
                        }

                        this._scoreLabelIndex = dict;
                    }
                }
            }
        }

        public ClassifyResult Classify(ushort channel, string message)
        {
            var pred = this.PredictionEngine?.Predict(new Data(channel, message));
            if (pred == null) return new ClassifyResult("UNKNOWN", 0f);
            var label = pred.Category ?? "UNKNOWN";
            float confidence = 0f;
            var scores = pred.Probabilities;
            if (scores != null && scores.Length > 0) {
                if (this._scoreLabelIndex != null && this._scoreLabelIndex.TryGetValue(label, out var idx) && idx >= 0 && idx < scores.Length) {
                    confidence = scores[idx];
                } else {
                    confidence = scores.Max();
                }
            }

            return new ClassifyResult(label, confidence);
        }

        public void Dispose()
        {
            this.PredictionEngine?.Dispose();
        }
    }
}
