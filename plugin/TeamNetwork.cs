namespace BotBuyPatch;

internal sealed class TeamNetwork(Dictionary<string, double[]> weights)
{
    private readonly Dictionary<string, float[]> tensors = weights.ToDictionary(e => e.Key, e => Array.ConvertAll(e.Value, v => (float)v));

    internal double[] Linear(string layer, double[] x, string separator = ".", int row = 0)
    {
        var w = tensors[layer + separator + "weight"];
        var b = tensors[layer + separator + "bias"];
        if (w.Length != x.Length * b.Length) throw new InvalidDataException("Invalid shape for " + layer);
        var input = Array.ConvertAll(x, v => (float)v);
        var y = new double[b.Length];
        // Match the float32 matrix and vector reduction paths of the reference runtime.
        bool vectorDot = b.Length < 8 || row == 4 && layer != "payer.0";
        for (int i = 0; i < y.Length; i++)
        {
            int offset = i * input.Length;
            float sum = 0;
            if (vectorDot)
            {
                float a = 0, c = 0, d = 0, e = 0; int j = 0;
                for (; j <= input.Length - 4; j += 4)
                {
                    a += w[offset + j] * input[j]; c += w[offset + j + 1] * input[j + 1];
                    d += w[offset + j + 2] * input[j + 2]; e += w[offset + j + 3] * input[j + 3];
                }
                sum = (a + d) + (c + e);
                for (; j < input.Length; j++) sum += w[offset + j] * input[j];
            }
            else
                for (int j = 0; j < input.Length; j++) sum = MathF.FusedMultiplyAdd(w[offset + j], input[j], sum);
            y[i] = sum + b[i];
        }
        return y;
    }
    internal static double[] Relu(double[] x) => Array.ConvertAll(x, v => Math.Max(0, v));
    internal static double[] Mean(double[][] x) => Enumerable.Range(0, x[0].Length).Select(i => x.Average(row => row[i])).ToArray();
    internal static double[] LogSoftmax(double[] x)
    {
        double max = x.Max(), log = Math.Log(x.Sum(v => Math.Exp(v - max)));
        return Array.ConvertAll(x, v => v - max - log);
    }
    internal static double[] FloatLogSoftmax(double[] logits)
    {
        float max = (float)logits.Max();
        Span<float> sums = stackalloc float[8];
        sums.Clear();
        for (int i = 0; i < logits.Length; i++) sums[i % 8] += MathF.Exp((float)logits[i] - max);
        float sum = 0;
        foreach (float partial in sums) sum += partial;
        float log = MathF.Log(sum);
        return Array.ConvertAll(logits, v => (double)(((float)v - max) - log));
    }
    internal static double Softplus(double x) => Math.Max(0, x) + Math.Log(1 + Math.Exp(-Math.Abs(x)));
    private double[] Normalize(string name, double[] x, double[] residual)
    {
        for (int i = 0; i < x.Length; i++) x[i] = (float)x[i] + (float)residual[i];
        // Eight float lanes, each with its own online moments, then merge the lanes.
        const int lanes = 8;
        int perLane = x.Length / lanes;
        float mean = 0, varianceSum = 0;
        for (int lane = 0; lane < lanes; lane++)
        {
            float localMean = 0, localSum = 0;
            for (int j = 0; j < perLane; j++)
            {
                float value = (float)x[j * lanes + lane], delta = value - localMean;
                localMean = MathF.FusedMultiplyAdd(delta, 1f / (j + 1), localMean);
                localSum = MathF.FusedMultiplyAdd(delta, value - localMean, localSum);
            }
            float change = localMean - mean, fraction = 1f / (lane + 1);
            mean += fraction * change;
            varianceSum += localSum + change * change * fraction * (lane * perLane);
        }
        float scale = 1 / MathF.Sqrt(varianceSum / x.Length + 1e-5f);
        var w = tensors[name + ".weight"]; var b = tensors[name + ".bias"];
        for (int i = 0; i < x.Length; i++) x[i] = ((float)x[i] - mean) * scale * w[i] + b[i];
        return x;
    }
    internal double[][] Encode(string project, double[][] x)
    {
        var input = x.Select((row, i) => Linear(project, row, row: i)).ToArray();
        var qkv = input.Select((row, i) => Linear("encoder.self_attn.in_proj", row, "_", i)).ToArray();
        int width = input[0].Length, head = width / 4;
        var result = new double[input.Length][];
        for (int i = 0; i < input.Length; i++)
        {
            var joined = new double[width];
            for (int offset = 0; offset < width; offset += head)
            {
                var scores = new float[input.Length];
                for (int j = 0; j < input.Length; j++)
                {
                    float a = 0, b = 0, c = 0, d = 0;
                    for (int k = offset; k < offset + head; k += 4)
                    {
                        a += (float)qkv[i][k] * (float)qkv[j][width + k];
                        b += (float)qkv[i][k + 1] * (float)qkv[j][width + k + 1];
                        c += (float)qkv[i][k + 2] * (float)qkv[j][width + k + 2];
                        d += (float)qkv[i][k + 3] * (float)qkv[j][width + k + 3];
                    }
                    scores[j] = ((a + c) + (b + d)) * (float)(1 / Math.Sqrt(head));
                }
                float max = scores.Max(), sum = 0;
                for (int j = 0; j < scores.Length; j++) { scores[j] = MathF.Exp(scores[j] - max); sum += scores[j]; }
                float inverse = 1 / sum;
                for (int k = offset; k < offset + head; k++)
                {
                    float Weighted(int j) => scores[j] * (float)qkv[j][2 * width + k];
                    float value = 0;
                    if (head == 8) value = (Weighted(0) + Weighted(1)) + (Weighted(2) + Weighted(3)) + Weighted(4);
                    else for (int j = 0; j < scores.Length; j++)
                        value = MathF.FusedMultiplyAdd(scores[j], (float)qkv[j][2 * width + k], value);
                    joined[k] = value * inverse;
                }
            }
            var h = Normalize("encoder.norm1", Linear("encoder.self_attn.out_proj", joined, row: i), input[i]);
            result[i] = Normalize("encoder.norm2", Linear("encoder.linear2", Relu(Linear("encoder.linear1", h, row: i)), row: i), h);
        }
        return result;
    }
}
