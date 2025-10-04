using Microsoft.Data.SqlClient;
using Microsoft.ML;
using Microsoft.ML.Data;
using System.Data;
using System.Linq;

public class LoginData
{
    public int UsuarioId { get; set; }
    public float Hora { get; set; }        // 0..23
    public float DiaSemana { get; set; }   // 0..6 (Sunday=0)
    public float IpGrupo { get; set; }
}

public class LoginPrediction
{
    [ColumnName("PredictedLabel")]
    public bool IsAnomaly { get; set; }

    [ColumnName("Score")]
    public float Score { get; set; }
}

class Program
{
    static void Main()
    {
        string connectionString = "Server=localhost;Database=master;Trusted_Connection=True;Encrypt=False;";
        var logins = ObterLoginsDoBanco(connectionString);

        if (logins.Count == 0)
        {
            Console.WriteLine("Nenhum login encontrado.");
            return;
        }

        Console.WriteLine($"Total de logins carregados: {logins.Count}");
        Console.WriteLine("Treinando modelo por usuário...\n");

        var mlContext = new MLContext();

        foreach (var grupo in logins.GroupBy(l => l.UsuarioId))
        {
            var usuarioId = grupo.Key;
            var dadosUsuario = grupo.ToList();

            if (dadosUsuario.Count < 5)
            {
                Console.WriteLine($"Usuário {usuarioId}: poucos logins ({dadosUsuario.Count}) — pulado.\n");
                continue;
            }

            // Frequências por variável (para explicabilidade)
            var freqHora = dadosUsuario.GroupBy(x => (int)x.Hora)
                .ToDictionary(g => g.Key, g => g.Count() / (float)dadosUsuario.Count);
            var freqDia = dadosUsuario.GroupBy(x => (int)x.DiaSemana)
                .ToDictionary(g => g.Key, g => g.Count() / (float)dadosUsuario.Count);
            var freqIp = dadosUsuario.GroupBy(x => (int)x.IpGrupo)
                .ToDictionary(g => g.Key, g => g.Count() / (float)dadosUsuario.Count);

            // Anti-NaN/sem variação
            bool variacaoSuficiente =
                freqHora.Count > 1 || freqDia.Count > 1 || freqIp.Count > 1;
            if (!variacaoSuficiente)
            {
                Console.WriteLine($"Usuário {usuarioId}: sem variação significativa — pulado.\n");
                continue;
            }

            // Pipeline PCA (features simples e estáveis)
            var dataView = mlContext.Data.LoadFromEnumerable(dadosUsuario);
            var pipeline = mlContext.Transforms.Concatenate("Features",
                        nameof(LoginData.Hora),
                        nameof(LoginData.DiaSemana),
                        nameof(LoginData.IpGrupo))
                    .Append(mlContext.Transforms.NormalizeMeanVariance("Features"))
                    .Append(mlContext.AnomalyDetection.Trainers.RandomizedPca(
                        featureColumnName: "Features",
                        rank: 2,
                        seed: 42));

            try
            {
                var model = pipeline.Fit(dataView);
                var transformed = model.Transform(dataView);

                // ✅ LER TIPADO (sem dynamic)
                var predictions = mlContext.Data.CreateEnumerable<LoginPrediction>(transformed, reuseRowObject: false).ToList();

                // p95 do usuário
                var rawScores = predictions.Select(p => float.IsNaN(p.Score) ? 0f : p.Score).ToList();
                float p95 = Percentil(rawScores, 0.95f);
                if (p95 <= 0f) p95 = rawScores.Average() + 2f * DesvioPadrao(rawScores);

                Console.WriteLine($"Usuário {usuarioId} -> p95={p95:F3}");

                for (int i = 0; i < dadosUsuario.Count; i++)
                {
                    var login = dadosUsuario[i];
                    var pred = predictions[i];

                    float score = float.IsNaN(pred.Score) ? 0f : pred.Score;

                    // PCA anômalo só se bem acima do p95
                    bool pcaAnomalo = score > p95 * 1.5f;

                    // Hora fora do padrão: raridade < 10%
                    bool foraHora = !freqHora.TryGetValue((int)login.Hora, out var fracHora) || fracHora < 0.10f;

                    // Se a hora for muito comum (>30%), não deixe só o PCA marcar
                    bool horaComum = fracHora >= 0.30f;
                    bool isSuspeito = (pcaAnomalo && !horaComum) || foraHora;

                    // Pesos de raridade (explicabilidade)
                    float pesoHora = 1 - freqHora.GetValueOrDefault((int)login.Hora, 0);
                    float pesoDia = 1 - freqDia.GetValueOrDefault((int)login.DiaSemana, 0);
                    float pesoIp = 1 - freqIp.GetValueOrDefault((int)login.IpGrupo, 0);

                    var fatores = new List<(string Nome, float Peso)>
                    {
                        ("Hora", pesoHora),
                        ("Dia da Semana", pesoDia),
                        ("IP", pesoIp)
                    };
                    var fatorPrincipal = fatores.OrderByDescending(f => f.Peso).First();

                    // Explicação textual
                    string explicacao = $"Maior desvio em {fatorPrincipal.Nome} (peso={fatorPrincipal.Peso:F2})";
                    if (pesoHora > 0.6f) explicacao += " | Horário pouco frequente";
                    if (pesoDia > 0.6f) explicacao += " | Dia da semana fora do padrão";
                    if (pesoIp > 0.6f) explicacao += " | IP raro para este usuário";

                    // Percentual normalizado (0..100)
                    float scorePercent = Math.Min(100, (p95 <= 0 ? 0 : (score / p95) * 100));

                    string risco = scorePercent switch
                    {
                        >= 70 => "ALTO",
                        >= 40 => "MÉDIO",
                        _ => "BAIXO"
                    };

                    string origem = pcaAnomalo && foraHora ? "Ambos"
                                   : pcaAnomalo ? "PCA"
                                   : foraHora ? "Hora"
                                   : "Nenhum";

                    string motivo = origem switch
                    {
                        "Ambos" => "Anomalia detectada pelo PCA e fora do horário padrão",
                        "PCA" => "Score PCA acima do limiar",
                        "Hora" => "Login fora do horário habitual do usuário",
                        _ => "Sem motivo"
                    };

                    if (isSuspeito)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ⚠️ SUSPEITO ({risco}) -> Hora={login.Hora}h, Score={score:F3}, Percent={scorePercent:F0}% ({origem})");
                        Console.WriteLine($"     Explicação: {explicacao}");
                        Console.ResetColor();

                        RegistrarAlerta(connectionString, usuarioId, login, score, scorePercent, risco, origem, motivo, explicacao);
                    }
                    else
                    {
                        Console.WriteLine($"  OK -> Hora={login.Hora}h, Score={score:F3}");
                    }
                }

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Usuário {usuarioId}: erro ao treinar ({ex.Message}) — ignorado.\n");
                continue;
            }
        }

        Console.WriteLine("Análise concluída.");
    }

    // ---------- Banco ----------

    static void RegistrarAlerta(
        string connectionString,
        int usuarioId,
        LoginData login,
        float score,
        float scorePercent,
        string risco,
        string origem,
        string motivo,
        string explicacao)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(@"
                INSERT INTO SecurityAlerts 
                (UsuarioId, HoraLogin, DiaSemana, IpGrupo, Score, ScorePercent, NivelRisco, FonteDeteccao, Motivo, Explicacao)
                VALUES (@UsuarioId, @HoraLogin, @DiaSemana, @IpGrupo, @Score, @ScorePercent, @NivelRisco, @FonteDeteccao, @Motivo, @Explicacao)", conn);

            cmd.Parameters.AddWithValue("@UsuarioId", usuarioId);
            cmd.Parameters.AddWithValue("@HoraLogin", (int)login.Hora);
            cmd.Parameters.AddWithValue("@DiaSemana", (int)login.DiaSemana);
            cmd.Parameters.AddWithValue("@IpGrupo", (int)login.IpGrupo);
            cmd.Parameters.AddWithValue("@Score", score);
            cmd.Parameters.AddWithValue("@ScorePercent", scorePercent);
            cmd.Parameters.AddWithValue("@NivelRisco", risco);
            cmd.Parameters.AddWithValue("@FonteDeteccao", origem);
            cmd.Parameters.AddWithValue("@Motivo", motivo);
            cmd.Parameters.AddWithValue("@Explicacao", explicacao);

            conn.Open();
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ Falha ao gravar alerta no banco: {ex.Message}");
        }
    }

    // ---------- Dados ----------

    static List<LoginData> ObterLoginsDoBanco(string connectionString)
    {
        var lista = new List<LoginData>();

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand("dbo.Sp_ConsultarLoginsRecentes", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        conn.Open();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var dataLogin = Convert.ToDateTime(reader["datalogin"]);

            lista.Add(new LoginData
            {
                UsuarioId = Convert.ToInt32(reader["usuarioid"]),
                Hora = dataLogin.Hour,
                DiaSemana = (float)dataLogin.DayOfWeek,
                IpGrupo = ExtrairGrupoIp(reader["ip"]?.ToString())
            });
        }

        return lista;
    }

    // ---------- Helpers ----------

    static float ExtrairGrupoIp(string? ip)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ip)) return 0;
            var partes = ip.Split('.');
            return int.TryParse(partes.FirstOrDefault(), out var grupo) ? grupo : 0;
        }
        catch { return 0; }
    }

    static float Percentil(List<float> values, float pFraction)
    {
        // pFraction em [0..1], ex.: 0.95
        if (values.Count == 0) return 0;
        var arr = values.OrderBy(x => x).ToArray();
        if (pFraction <= 0) return arr.First();
        if (pFraction >= 1) return arr.Last();
        double pos = (arr.Length - 1) * pFraction;
        int idx = (int)Math.Floor(pos);
        double frac = pos - idx;
        return idx + 1 < arr.Length
            ? (float)(arr[idx] + frac * (arr[idx + 1] - arr[idx]))
            : arr[idx];
    }

    static float DesvioPadrao(List<float> values)
    {
        if (values.Count == 0) return 0;
        float media = values.Average();
        double var = values.Average(v => Math.Pow(v - media, 2));
        return (float)Math.Sqrt(var);
    }
}
