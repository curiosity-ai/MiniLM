using BERTTokenizers.Base;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using static MiniLM.DenseTensorHelpers;
using MiniLM.Shared;

namespace MiniLM;

public sealed class SentenceEncoder : IDisposable
{
    private readonly SessionOptions   _sessionOptions;
    private readonly InferenceSession _session;
    private readonly TokenizerBase    _tokenizer;
    private readonly string[]         _outputNames;

    public SentenceEncoder(SessionOptions sessionOptions = null)
    {
        _sessionOptions = sessionOptions ?? new SessionOptions();
        _session        = new InferenceSession(ResourceLoader.GetResource(typeof(SentenceEncoder).Assembly, "model.onnx"), _sessionOptions);
        _tokenizer      = new MiniLMTokenizer();
        _outputNames    = _session.OutputMetadata.Keys.ToArray();
    }

    public new void Dispose()
    {
        _sessionOptions.Dispose();
        _session.Dispose();
    }

    public EncodedChunk[] ChunkAndEncode(string text, int chunkLength = 500, int chunkOverlap = 100, bool sequentially = true, int maxChunks = int.MaxValue, CancellationToken cancellationToken = default)
        => SentenceEncoderBase.ChunkAndEncode(Encode, text, chunkLength, chunkOverlap, sequentially, maxChunks, cancellationToken);

    public TaggedEncodedChunk[] ChunkAndEncodeTagged(string text, Func<string, TaggedChunk> stripTags, int chunkLength = 500, int chunkOverlap = 100, bool sequentially = true, int maxChunks = int.MaxValue, CancellationToken cancellationToken = default)
        => SentenceEncoderBase.ChunkAndEncodeTagged(Encode, text, stripTags, chunkLength, chunkOverlap, sequentially, maxChunks, cancellationToken);

    public static List<string> ChunkText(string text, char separator = ' ', int chunkLength = 500, int chunkOverlap = 100, int maxChunks = int.MaxValue)
        => SentenceEncoderBase.ChunkText(text, separator, chunkLength, chunkOverlap, maxChunks);

    public float[][] Encode(string[] sentences, CancellationToken cancellationToken = default)
    {
        var numSentences = sentences.Length;

        var encoded    = _tokenizer.Encode(sentences);
        var tokenCount = encoded.First().InputIds.Length;

        long[] flattenIDs           = new long[encoded.Sum(s => s.InputIds.Length)];
        long[] flattenAttentionMask = new long[encoded.Sum(s => s.AttentionMask.Length)];
        long[] flattenTokenTypeIds  = new long[encoded.Sum(s => s.TokenTypeIds.Length)];

        var flattenIDsSpan           = flattenIDs.AsSpan();
        var flattenAttentionMaskSpan = flattenAttentionMask.AsSpan();
        var flattenTokenTypeIdsSpan  = flattenTokenTypeIds.AsSpan();

        foreach (var (InputIds, TokenTypeIds, AttentionMask) in encoded)
        {
            InputIds.AsSpan().CopyTo(flattenIDsSpan);
            flattenIDsSpan = flattenIDsSpan.Slice(InputIds.Length);

            AttentionMask.AsSpan().CopyTo(flattenAttentionMaskSpan);
            flattenAttentionMaskSpan = flattenAttentionMaskSpan.Slice(AttentionMask.Length);

            TokenTypeIds.AsSpan().CopyTo(flattenTokenTypeIdsSpan);
            flattenTokenTypeIdsSpan = flattenTokenTypeIdsSpan.Slice(TokenTypeIds.Length);
        }

        var dimensions = new[] { numSentences, tokenCount };

        var input = new NamedOnnxValue[3]
        {
            NamedOnnxValue.CreateFromTensor("input_ids",      new DenseTensor<long>(flattenIDs,           dimensions)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(flattenAttentionMask, dimensions)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(flattenTokenTypeIds,  dimensions))
        };

        using var runOptions   = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);

        using var output = _session.Run(input, _outputNames, runOptions);

        cancellationToken.ThrowIfCancellationRequested();

        var output_pooled            = MeanPooling((DenseTensor<float>)output.First().Value, encoded);
        var output_pooled_normalized = Normalize(output_pooled);

        const int embDim = 384;

        var outputFlatten = new float[sentences.Length][];

        for (int s = 0; s < sentences.Length; s++)
        {
            var emb = new float[embDim];
            outputFlatten[s] = emb;

            for (int i = 0; i < embDim; i++)
            {
                emb[i] = output_pooled_normalized[s, i];
            }
        }

        return outputFlatten;
    }
}