using System.Text;
using AITradingSystem.Common.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>Тип KV-кэша для расчёта VRAM (llama.cpp type_k/type_v).</summary>
public enum KvCacheType
{
    F16,
    Q8_0,
    Q4_0
}

/// <summary>Один тензор GGUF: имя, тип квантования, размеры, точный размер в байтах.</summary>
public sealed class GgufTensorInfo
{
    public string Name { get; init; } = string.Empty;
    public int Type { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public ulong[] Dimensions { get; init; } = Array.Empty<ulong>();
    public ulong Offset { get; init; }
    public long ByteSize { get; init; }

    public ulong ElementCount
    {
        get
        {
            ulong n = 1;
            foreach (var d in Dimensions) n *= d;
            return n;
        }
    }
}

/// <summary>
/// A2. Метаданные GGUF-модели и точные размеры весов, прочитанные БЕЗ загрузки модели.
/// Используется QwenModel до LLamaWeights.LoadFromFile для VRAM-префлайта (AutoFitGpu).
/// </summary>
public sealed class GgufModelInfo
{
    public string FilePath { get; init; } = string.Empty;
    public int Version { get; init; }
    public string Architecture { get; init; } = "unknown";
    public int BlockCount { get; init; }
    public long ContextLength { get; init; }
    public long EmbeddingLength { get; init; }
    public long HeadCount { get; init; }
    public long HeadCountKv { get; init; }
    public long KeyLength { get; init; }
    public long ValueLength { get; init; }
    public long VocabSize { get; init; }
    public long Alignment { get; init; } = 32;

    public IReadOnlyList<GgufTensorInfo> Tensors { get; init; } = Array.Empty<GgufTensorInfo>();
    public long WeightBytesTotal { get; init; }
    public long TokenEmbeddingBytes { get; init; }
    public long OutputWeightBytes { get; init; }
    public long MaxTensorBytes { get; init; }
    public long BytesPerLayer { get; init; }
    public long DataStartOffset { get; init; }

    public double WeightMb => WeightBytesTotal / 1024.0 / 1024.0;
    public double TokenEmbeddingMb => TokenEmbeddingBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Байт KV-кэша на один токен контекста (все слои, K+V) для заданного типа кэша.
    /// Считается по реальным head_count_kv и key/value length из метаданных.
    /// </summary>
    public long KvBytesPerToken(KvCacheType kvType = KvCacheType.F16)
    {
        var layers = BlockCount > 0 ? BlockCount : 1;
        var kvHeads = HeadCountKv > 0 ? HeadCountKv : (HeadCount > 0 ? HeadCount : 1);
        var kLen = KeyLength > 0 ? KeyLength : (HeadCount > 0 ? EmbeddingLength / HeadCount : 0);
        var vLen = ValueLength > 0 ? ValueLength : kLen;
        var elemsPerLayer = kvHeads * (kLen + vLen);

        double bytesPerElem = kvType switch
        {
            KvCacheType.Q8_0 => 34.0 / 32.0, // 34 байта на блок из 32 элементов
            KvCacheType.Q4_0 => 18.0 / 32.0, // 18 байт на блок из 32 элементов
            _ => 2.0                         // f16
        };

        return (long)Math.Ceiling(layers * elemsPerLayer * bytesPerElem);
    }

    /// <summary>Полный KV-кэш на ctx токенов, MiB.</summary>
    public double KvCacheMb(int ctx, KvCacheType kvType = KvCacheType.F16) =>
        KvBytesPerToken(kvType) * (double)ctx / 1024.0 / 1024.0;

    /// <summary>
    /// Оценка compute-буферов (MiB): граф, активации и attention scores.
    /// Без FlashAttention scores занимают batch·ctx·heads·4 байт — главная статья при большом ctx.
    /// Оценка консервативная (сверху), точные значения приходят из нативного лога после загрузки.
    /// </summary>
    public double ComputeBufferEstimate(int batch, int ctx, bool flashAttn)
    {
        var heads = HeadCount > 0 ? HeadCount : 32;
        var embed = EmbeddingLength > 0 ? EmbeddingLength : 4096;
        var vocab = VocabSize > 0 ? VocabSize : 150_000;

        double scores = flashAttn
            ? batch * (double)ctx * heads * 2          // flash: компактные промежуточные буферы
            : batch * (double)ctx * heads * 4;         // f32 attention scores

        double activations = batch * (double)embed * 16;  // несколько промежуточных теноров на токен батча
        double logits = batch * (double)vocab * 4;        // выходной logits-буфер
        const double graphOverhead = 128 * 1024 * 1024;   // граф, резервы, выравнивания

        return (scores + activations + logits + graphOverhead) / 1024.0 / 1024.0;
    }

    /// <summary>
    /// Максимум слоёв, влезающий в VRAM: бюджет = free − reserve − compute-буферы −
    /// «нелейерные» тензоры (token_embd, output); остаток делим на (вес слоя + KV слоя).
    /// KV учитывается РОВНО ОДИН раз — по числу оффлоаженных слоёв (llama.cpp размещает
    /// KV слоя на том устройстве, где считается слой). clamp [0; BlockCount].
    /// maxSingleAllocMb &gt; 0 — лимит одной аллокации (legacy AMD Polaris ~1024 MB):
    /// если token_embd.weight больше лимита, GPU-оффлоад невозможен → 0.
    /// </summary>
    public int RecommendGpuLayers(
        long vramFreeMb,
        long reserveMb,
        int ctx,
        bool flashAttn,
        KvCacheType kvType = KvCacheType.F16,
        int batch = 512,
        long maxSingleAllocMb = 0)
    {
        if (BlockCount <= 0 || BytesPerLayer <= 0)
            return 0;

        if (maxSingleAllocMb > 0 && TokenEmbeddingBytes > maxSingleAllocMb * 1024L * 1024L)
        {
            AiLog.Backend(
                $"GGUF: token_embd.weight {TokenEmbeddingMb:F0} MiB > лимита одной аллокации {maxSingleAllocMb} MiB " +
                "(legacy AMD) — GPU-оффлоад невозможен, рекомендуется CPU");
            return 0;
        }

        var budgetMb = vramFreeMb - reserveMb;
        if (budgetMb <= 0) return 0;

        var kvMb = KvCacheMb(ctx, kvType);
        var computeMb = ComputeBufferEstimate(batch, ctx, flashAttn);
        var nonLayerMb = (TokenEmbeddingBytes + OutputWeightBytes) / 1024.0 / 1024.0;

        // A2: KV НЕ вычитается целиком заранее — иначе он учитывался дважды
        // (полный KV в бюджете + kvPerLayer в делителе) и число слоёв занижалось.
        var availableForLayersMb = budgetMb - computeMb - nonLayerMb;
        if (availableForLayersMb <= 0) return 0;

        var kvPerLayerMb = kvMb / BlockCount;
        var layerWeightMb = BytesPerLayer / 1024.0 / 1024.0;
        var perLayerMb = layerWeightMb + kvPerLayerMb;
        if (perLayerMb <= 0) return 0;

        var layers = (int)Math.Floor(availableForLayersMb / perLayerMb);
        return Math.Clamp(layers, 0, BlockCount);
    }

    /// <summary>Влезает ли модель целиком (все слои + KV + compute + не-слойные тензоры) в VRAM.</summary>
    public bool CanFullyOffload(
        long vramFreeMb,
        long reserveMb,
        int ctx,
        bool flashAttn,
        KvCacheType kvType = KvCacheType.F16,
        int batch = 512,
        long maxSingleAllocMb = 0)
    {
        if (maxSingleAllocMb > 0 && MaxTensorBytes > maxSingleAllocMb * 1024L * 1024L)
            return false;

        var totalMb = WeightMb + KvCacheMb(ctx, kvType) + ComputeBufferEstimate(batch, ctx, flashAttn);
        return totalMb <= vramFreeMb - reserveMb;
    }

    public string Summary() =>
        $"GGUF {Architecture} v{Version}: layers={BlockCount}, ctx={ContextLength}, embed={EmbeddingLength}, " +
        $"heads={HeadCount}/kv={HeadCountKv}, vocab={VocabSize}; weights={WeightMb:F0} MiB, " +
        $"token_embd={TokenEmbeddingMb:F0} MiB, maxTensor={MaxTensorBytes / 1024.0 / 1024.0:F0} MiB, " +
        $"perLayer≈{BytesPerLayer / 1024.0 / 1024.0:F1} MiB";
}

/// <summary>
/// Парсер заголовка/метаданных GGUF (v2/v3) без загрузки модели в llama.
/// Читает только заголовок, KV-метаданные и таблицу тензоров — сами веса не трогаются.
/// </summary>
public static class GgufInspector
{
    /// <summary>Прочитать метаданные модели. Бросает исключение только при повреждённом файле.</summary>
    public static GgufModelInfo Inspect(string modelPath)
    {
        using var fs = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs, Encoding.UTF8);

        var magic = reader.ReadUInt32();
        if (magic != 0x46554747) // "GGUF" little-endian
            throw new InvalidDataException($"Не GGUF-файл: {modelPath} (magic=0x{magic:X8})");

        var version = reader.ReadInt32();
        if (version is < 2 or > 3)
            throw new InvalidDataException($"GGUF version {version} не поддерживается (ожидается 2 или 3)");

        var tensorCount = reader.ReadUInt64();
        var kvCount = reader.ReadUInt64();

        if (tensorCount > 100_000 || kvCount > 100_000)
            throw new InvalidDataException("GGUF: неправдоподобные счётчики — файл повреждён?");

        var meta = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(reader);
            var type = reader.ReadInt32();
            var value = ReadValue(reader, type, depth: 0);
            // Массивы (токены/мерджи) не храним — только скаляры и длина vocab.
            if (value is GgufArrayLength len)
                meta[key + ":length"] = len.Length;
            else
                meta[key] = value;
        }

        var tensors = new List<GgufTensorInfo>((int)Math.Min(tensorCount, 100_000));
        long weightTotal = 0, tokenEmb = 0, outputWeight = 0, maxTensor = 0, layerBytes = 0;
        var layerTensorCount = 0;

        for (ulong i = 0; i < tensorCount; i++)
        {
            var name = ReadString(reader);
            var nDims = reader.ReadInt32();
            if (nDims is < 0 or > 8)
                throw new InvalidDataException($"GGUF: тензор {name} с nDims={nDims}");

            var dims = new ulong[nDims];
            for (var d = 0; d < nDims; d++)
                dims[d] = reader.ReadUInt64();

            var type = reader.ReadInt32();
            var offset = reader.ReadUInt64();

            var byteSize = ComputeTensorBytes(dims, type);
            var info = new GgufTensorInfo
            {
                Name = name,
                Type = type,
                TypeName = GgmlTypeName(type),
                Dimensions = dims,
                Offset = offset,
                ByteSize = byteSize
            };
            tensors.Add(info);

            weightTotal += byteSize;
            if (byteSize > maxTensor) maxTensor = byteSize;
            if (name == "token_embd.weight") tokenEmb = byteSize;
            if (name == "output.weight") outputWeight = byteSize;
            if (name.StartsWith("blk.", StringComparison.Ordinal))
            {
                layerBytes += byteSize;
                layerTensorCount++;
            }
        }

        var alignment = GetLong(meta, "general.alignment", 32);
        var pos = fs.Position;
        var dataStart = (pos + alignment - 1) / alignment * alignment;

        var arch = GetString(meta, "general.architecture", "unknown");
        var blockCount = (int)GetLong(meta, $"{arch}.block_count", 0);
        var headCount = GetLong(meta, $"{arch}.attention.head_count", 0);
        var embed = GetLong(meta, $"{arch}.embedding_length", 0);
        var keyLen = GetLong(meta, $"{arch}.attention.key_length", headCount > 0 ? embed / headCount : 0);
        var valLen = GetLong(meta, $"{arch}.attention.value_length", keyLen);

        // output.weight может быть tied (отсутствует) — тогда его роль играет token_embd.
        if (outputWeight == 0) outputWeight = tokenEmb;

        var bytesPerLayer = blockCount > 0
            ? layerBytes / blockCount
            : (layerTensorCount > 0 ? layerBytes / Math.Max(1, layerTensorCount / 10) : 0);

        return new GgufModelInfo
        {
            FilePath = modelPath,
            Version = version,
            Architecture = arch,
            BlockCount = blockCount,
            ContextLength = GetLong(meta, $"{arch}.context_length", 0),
            EmbeddingLength = embed,
            HeadCount = headCount,
            HeadCountKv = GetLong(meta, $"{arch}.attention.head_count_kv", headCount),
            KeyLength = keyLen,
            ValueLength = valLen,
            VocabSize = GetLong(meta, $"{arch}.vocab_size", GetLong(meta, "tokenizer.ggml.tokens:length", 0)),
            Alignment = alignment,
            Tensors = tensors,
            WeightBytesTotal = weightTotal,
            TokenEmbeddingBytes = tokenEmb,
            OutputWeightBytes = outputWeight,
            MaxTensorBytes = maxTensor,
            BytesPerLayer = bytesPerLayer,
            DataStartOffset = dataStart
        };
    }

    /// <summary>Безопасная обёртка: null вместо исключения (повреждённый/отсутствующий файл).</summary>
    public static GgufModelInfo? TryInspect(string modelPath)
    {
        try
        {
            return Inspect(modelPath);
        }
        catch (Exception ex)
        {
            AiLog.Backend($"GGUF inspect не удался для {modelPath}: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Размеры тензоров по типам квантования ggml
    // ------------------------------------------------------------------

    private static long ComputeTensorBytes(ulong[] dims, int type)
    {
        ulong elements = 1;
        foreach (var d in dims) elements *= d;

        var (blockSize, typeSize) = GgmlTypeSize(type);
        if (blockSize == 0) return 0;
        return (long)(elements / (ulong)blockSize * (ulong)typeSize);
    }

    /// <summary>(blockSize, bytesPerBlock) для типов ggml. Неизвестный тип → (1, 4) как f32.</summary>
    private static (int Block, int Bytes) GgmlTypeSize(int type) => type switch
    {
        0 => (1, 4),    // F32
        1 => (1, 2),    // F16
        2 => (32, 18),  // Q4_0
        3 => (32, 20),  // Q4_1
        6 => (32, 22),  // Q5_0
        7 => (32, 24),  // Q5_1
        8 => (32, 34),  // Q8_0
        9 => (32, 36),  // Q8_1
        10 => (256, 84),  // Q2_K
        11 => (256, 110), // Q3_K
        12 => (256, 144), // Q4_K
        13 => (256, 176), // Q5_K
        14 => (256, 210), // Q6_K
        15 => (256, 292), // Q8_K
        16 => (256, 66),  // IQ2_XXS
        17 => (256, 74),  // IQ2_XS
        18 => (256, 98),  // IQ3_XXS
        19 => (256, 50),  // IQ1_S
        20 => (32, 18),   // IQ4_NL
        21 => (256, 110), // IQ3_S
        22 => (256, 82),  // IQ2_S
        23 => (256, 136), // IQ4_XS
        24 => (1, 1),     // I8
        25 => (1, 2),     // I16
        26 => (1, 4),     // I32
        27 => (1, 8),     // I64
        28 => (1, 8),     // F64
        29 => (256, 56),  // IQ1_M
        30 => (1, 2),     // BF16
        _ => (1, 4)
    };

    private static string GgmlTypeName(int type) => type switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 6 => "Q5_0", 7 => "Q5_1",
        8 => "Q8_0", 9 => "Q8_1", 10 => "Q2_K", 11 => "Q3_K", 12 => "Q4_K", 13 => "Q5_K",
        14 => "Q6_K", 15 => "Q8_K", 16 => "IQ2_XXS", 17 => "IQ2_XS", 18 => "IQ3_XXS",
        19 => "IQ1_S", 20 => "IQ4_NL", 21 => "IQ3_S", 22 => "IQ2_S", 23 => "IQ4_XS",
        24 => "I8", 25 => "I16", 26 => "I32", 27 => "I64", 28 => "F64", 29 => "IQ1_M", 30 => "BF16",
        _ => $"type{type}"
    };

    // ------------------------------------------------------------------
    // Чтение GGUF-значений
    // ------------------------------------------------------------------

    private sealed record GgufArrayLength(long Length);

    private static string ReadString(BinaryReader reader)
    {
        var len = reader.ReadUInt64();
        if (len > 64 * 1024 * 1024)
            throw new InvalidDataException($"GGUF: неправдоподобная длина строки {len}");
        var bytes = reader.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static object ReadValue(BinaryReader reader, int type, int depth)
    {
        if (depth > 8)
            throw new InvalidDataException("GGUF: слишком глубокая вложенность массивов");

        switch (type)
        {
            case 0: return reader.ReadByte();          // UINT8
            case 1: return reader.ReadSByte();         // INT8
            case 2: return reader.ReadUInt16();        // UINT16
            case 3: return reader.ReadInt16();         // INT16
            case 4: return reader.ReadUInt32();        // UINT32
            case 5: return reader.ReadInt32();         // INT32
            case 6: return reader.ReadSingle();        // FLOAT32
            case 7: return reader.ReadBoolean();       // BOOL
            case 8: return ReadString(reader);         // STRING
            case 9:                                    // ARRAY
            {
                var elemType = reader.ReadInt32();
                var count = reader.ReadUInt64();
                if (count > 10_000_000)
                    throw new InvalidDataException($"GGUF: неправдоподобная длина массива {count}");
                for (ulong i = 0; i < count; i++)
                    ReadValue(reader, elemType, depth + 1);
                return new GgufArrayLength((long)count);
            }
            case 10: return reader.ReadUInt64();       // UINT64
            case 11: return reader.ReadInt64();        // INT64
            case 12: return reader.ReadDouble();       // FLOAT64
            default:
                throw new InvalidDataException($"GGUF: неизвестный тип метаданных {type}");
        }
    }

    private static long GetLong(Dictionary<string, object> meta, string key, long fallback)
    {
        if (!meta.TryGetValue(key, out var value)) return fallback;
        return value switch
        {
            byte v => v,
            sbyte v => v,
            ushort v => v,
            short v => v,
            uint v => v,
            int v => v,
            ulong v => (long)v,
            long v => v,
            float v => (long)v,
            double v => (long)v,
            _ => fallback
        };
    }

    private static string GetString(Dictionary<string, object> meta, string key, string fallback) =>
        meta.TryGetValue(key, out var value) && value is string s ? s : fallback;
}
