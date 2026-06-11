using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FluxCore
{
    /// <summary>
    /// Local CPU sentence embeddings using all-MiniLM-L6-v2 (384-dim).
    /// Downloads model.onnx (~90 MB) and vocab.txt to %APPDATA%\Davos\models\ on first run.
    /// Thread-safe after initialization: InferenceSession supports concurrent Run() calls.
    /// Implements graceful degradation — IsReady=false until async init completes.
    /// </summary>
    public sealed class LocalEmbeddingService : IDisposable
    {
        private readonly string _modelsDir;
        private InferenceSession? _session;
        private BertTokenizer?   _tokenizer;
        private readonly SemaphoreSlim _initLock = new(1, 1);

        public const int Dims = 384;
        public bool IsReady { get; private set; }

        private const string ModelUrl =
            "https://huggingface.co/Qdrant/all-MiniLM-L6-v2-onnx/resolve/main/model.onnx";
        private const string VocabUrl =
            "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";
        private const int MaxSeqLen = 512;

        public LocalEmbeddingService()
        {
            _modelsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Davos", "models");
            Directory.CreateDirectory(_modelsDir);
        }

        /// <summary>
        /// Downloads model files if missing, then loads ONNX session and tokenizer.
        /// Safe to call multiple times — only initializes once.
        /// </summary>
        public async Task EnsureInitializedAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (IsReady) return;

                await DownloadIfMissingAsync("model.onnx", ModelUrl);
                await DownloadIfMissingAsync("vocab.txt",  VocabUrl);

                string modelPath = Path.Combine(_modelsDir, "model.onnx");
                string vocabPath = Path.Combine(_modelsDir, "vocab.txt");

                var opts = new SessionOptions();
                opts.InterOpNumThreads  = 1;
                opts.IntraOpNumThreads  = 2;
                opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                _session = new InferenceSession(modelPath, opts);

                // Log actual output names so the user can verify "last_hidden_state"
                System.Diagnostics.Debug.WriteLine(
                    $"[LocalEmbed] ONNX inputs : {string.Join(", ", _session.InputMetadata.Keys)}");
                System.Diagnostics.Debug.WriteLine(
                    $"[LocalEmbed] ONNX outputs: {string.Join(", ", _session.OutputMetadata.Keys)}");

                _tokenizer = new BertTokenizer();
                await _tokenizer.LoadVocabularyAsync(vocabPath, true);

                IsReady = true;
                System.Diagnostics.Debug.WriteLine("[LocalEmbed] Ready — all-MiniLM-L6-v2 loaded.");
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Returns a 384-dim L2-normalized sentence embedding.
        /// Returns empty array if not initialized or on any error (silent degradation).
        /// </summary>
        public float[] GetEmbedding(string text)
        {
            if (!IsReady || _session == null || _tokenizer == null) return Array.Empty<float>();
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();

            try
            {
                var (inputIds, attentionMask, tokenTypeIds) =
                    _tokenizer.Encode(text, maximumTokens: MaxSeqLen);

                int seqLen = inputIds.Length;

                var inputIdsTensor      = new DenseTensor<long>(new[] { 1, seqLen });
                var attentionMaskTensor = new DenseTensor<long>(new[] { 1, seqLen });
                var tokenTypeIdsTensor  = new DenseTensor<long>(new[] { 1, seqLen });

                var idSpan   = inputIds.Span;
                var maskSpan = attentionMask.Span;
                var typeSpan = tokenTypeIds.Span;

                for (int i = 0; i < seqLen; i++)
                {
                    inputIdsTensor[0, i]      = idSpan[i];
                    attentionMaskTensor[0, i] = maskSpan[i];
                    tokenTypeIdsTensor[0, i]  = typeSpan[i];
                }

                var namedInputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids",      inputIdsTensor),
                    NamedOnnxValue.CreateFromTensor("attention_mask",  attentionMaskTensor),
                    NamedOnnxValue.CreateFromTensor("token_type_ids",  tokenTypeIdsTensor),
                };

                using var results = _session.Run(namedInputs);

                // The Qdrant all-MiniLM-L6-v2 ONNX exports "last_hidden_state"
                // Shape: [1, seqLen, 384]
                var lastHidden = results.First().AsTensor<float>();

                // Mean pooling: average token embeddings weighted by attention mask
                var pooled  = new float[Dims];
                float maskSum = 0f;
                for (int t = 0; t < seqLen; t++)
                {
                    float m = (float)maskSpan[t];
                    maskSum += m;
                    for (int d = 0; d < Dims; d++)
                        pooled[d] += lastHidden[0, t, d] * m;
                }
                if (maskSum > 0f)
                    for (int d = 0; d < Dims; d++)
                        pooled[d] /= maskSum;

                // L2 normalize
                float norm = 0f;
                for (int d = 0; d < Dims; d++) norm += pooled[d] * pooled[d];
                norm = MathF.Sqrt(norm);
                if (norm > 1e-9f)
                    for (int d = 0; d < Dims; d++)
                        pooled[d] /= norm;

                return pooled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalEmbed] GetEmbedding error: {ex.Message}");
                return Array.Empty<float>();
            }
        }

        private async Task DownloadIfMissingAsync(string filename, string url)
        {
            string path = Path.Combine(_modelsDir, filename);
            if (File.Exists(path)) return;

            System.Diagnostics.Debug.WriteLine($"[LocalEmbed] Downloading {filename}...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            System.Diagnostics.Debug.WriteLine(
                $"[LocalEmbed] Downloaded {filename} ({bytes.Length / 1024} KB)");
        }

        public void Dispose()
        {
            _session?.Dispose();
            _initLock.Dispose();
        }
    }
}
