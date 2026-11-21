using System.Text.Json;

namespace BotBuyPatch;

internal sealed record PreviousBuy(int Round, double[] Features, int Action);

// Inference for the fixed sequence.py architecture. Only real history tokens enter
// attention; their learned positions stay right-aligned exactly as during training.
internal sealed class ProTransformer
{
    public required int Version { get; init; }
    public required string Kind { get; init; }
    public required int Window { get; init; }
    public required string[] Actions { get; init; }
    public required string[] Features { get; init; }
    public required double[] Scales { get; init; }
    public required Dictionary<string, int> Names { get; init; }
    public required Dictionary<string, double[]> Weights { get; init; }

    internal static ProTransformer Load(string path, IEnumerable<string> primaries)
    {
        var model = JsonSerializer.Deserialize<ProTransformer>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (model is null || model.Version != 1 || model.Kind != "transformer" || model.Window != 4 ||
            !model.Features.SequenceEqual(new[] { "cash", "team_cash", "team_primaries", "armor", "last_round" }) ||
            !model.Scales.SequenceEqual(new double[] { 4000, 4000, 4, 1, 1 }) ||
            !model.Actions.SequenceEqual(primaries.Order(StringComparer.Ordinal).Prepend("none")) ||
            model.Names.Values.Any(i => i is < 0 or > 3))
            throw new InvalidDataException("Incompatible primary-buy Transformer.");
        void Tensor(string key, int size)
        {
            if (!model.Weights.TryGetValue(key, out var values) || values.Length != size || values.Any(x => !double.IsFinite(x)))
                throw new InvalidDataException($"Invalid Transformer tensor {key}.");
        }
        void Linear(string key, int input, int output, string separator = ".")
        {
            Tensor(key + separator + "weight", input * output);
            Tensor(key + separator + "bias", output);
        }
        Tensor("position", 5 * 32);
        Linear("current.0", 10, 64);
        Linear("current.2", 64, 32);
        Linear("historical", 6 + model.Actions.Length, 32);
        Linear("head.1", 32, model.Actions.Length);
        for (int layer = 0; layer < 2; layer++)
        {
            string prefix = $"encoder.layers.{layer}.";
            Linear(prefix + "self_attn.in_proj", 32, 96, "_");
            Linear(prefix + "self_attn.out_proj", 32, 32);
            Linear(prefix + "linear1", 32, 64);
            Linear(prefix + "linear2", 64, 32);
            Linear(prefix + "norm1", 1, 32);
            Linear(prefix + "norm2", 1, 32);
        }
        return model;
    }

    private double[] Linear(string key, double[] input, string separator = ".")
    {
        var weights = Weights[key + separator + "weight"];
        var result = (double[])Weights[key + separator + "bias"].Clone();
        for (int i = 0; i < result.Length; i++)
            for (int j = 0; j < input.Length; j++)
                result[i] += weights[i * input.Length + j] * input[j];
        return result;
    }

    private double[] NormalizeResidual(string key, double[] residual, double[] output)
    {
        for (int i = 0; i < output.Length; i++) output[i] += residual[i];
        double mean = output.Average();
        double scale = 1 / Math.Sqrt(output.Sum(x => (x - mean) * (x - mean)) / output.Length + 1e-5);
        var weight = Weights[key + ".weight"];
        var bias = Weights[key + ".bias"];
        for (int i = 0; i < output.Length; i++) output[i] = (output[i] - mean) * scale * weight[i] + bias[i];
        return output;
    }

    private static void Softmax(double[] values)
    {
        double max = values.Max();
        for (int i = 0; i < values.Length; i++) values[i] = Math.Exp(values[i] - max);
        double sum = values.Sum();
        for (int i = 0; i < values.Length; i++) values[i] /= sum;
    }

    private static double[] Attention(double[][] qkv, int token)
    {
        var output = new double[32];
        var scores = new double[qkv.Length];
        for (int offset = 0; offset < 32; offset += 8)
        {
            for (int j = 0; j < qkv.Length; j++)
            {
                double score = 0;
                for (int k = offset; k < offset + 8; k++) score += qkv[token][k] * qkv[j][32 + k];
                scores[j] = score / Math.Sqrt(8);
            }
            Softmax(scores);
            for (int j = 0; j < qkv.Length; j++)
                for (int k = offset; k < offset + 8; k++) output[k] += scores[j] * qkv[j][64 + k];
        }
        return output;
    }

    // GELU uses the standard normal CDF. erf approximation error is below 1.5e-7.
    private static double Gelu(double x)
    {
        double z = Math.Abs(x) / Math.Sqrt(2), t = 1 / (1 + 0.3275911 * z);
        double tail = (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t
            - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return x * 0.5 * (x >= 0 ? 2 - tail : tail);
    }

    internal double[] Probabilities(string side, double[] features, string name, int round, IReadOnlyList<PreviousBuy> history)
    {
        var input = new double[10];
        for (int k = 0; k < 5; k++) input[k] = features[k] / Scales[k];
        input[5] = side == "3" ? 1 : 0;
        if (Names.TryGetValue(name.Trim().ToLowerInvariant(), out int role)) input[6 + role] = 1;
        var current = Linear("current.2", Array.ConvertAll(Linear("current.0", input), x => Math.Max(0, x)));
        var tokens = new double[history.Count + 1][];
        for (int i = 0; i < history.Count; i++)
        {
            var h = history[i];
            var previous = new double[6 + Actions.Length];
            for (int k = 0; k < 5; k++) previous[k] = h.Features[k] / Scales[k];
            previous[5] = (round - h.Round) / 12.0;
            previous[6 + h.Action] = 1;
            tokens[i] = Linear("historical", previous);
        }
        tokens[^1] = (double[])current.Clone();
        var position = Weights["position"];
        for (int i = 0; i < tokens.Length; i++)
            for (int k = 0; k < 32; k++) tokens[i][k] += position[(5 - tokens.Length + i) * 32 + k];
        for (int layer = 0; layer < 2; layer++)
        {
            string prefix = $"encoder.layers.{layer}.";
            var qkv = Array.ConvertAll(tokens, t => Linear(prefix + "self_attn.in_proj", t, "_"));
            for (int i = 0; i < tokens.Length; i++)
            {
                var attention = Linear(prefix + "self_attn.out_proj", Attention(qkv, i));
                var hidden = NormalizeResidual(prefix + "norm1", tokens[i], attention);
                var ff = Linear(prefix + "linear2", Array.ConvertAll(Linear(prefix + "linear1", hidden), Gelu));
                tokens[i] = NormalizeResidual(prefix + "norm2", hidden, ff);
            }
        }
        for (int k = 0; k < 32; k++) current[k] = Math.Max(0, current[k] + tokens[^1][k]);
        var p = Linear("head.1", current);
        Softmax(p);
        return p;
    }
}
