CREATE TABLE SecurityAlerts (
    Id INT IDENTITY(1,1) PRIMARY KEY,          -- Identificador único incremental
    UsuarioId INT NOT NULL,                    -- Código do usuário associado ao alerta
    DataHora DATETIME NOT NULL DEFAULT GETDATE(), -- Momento em que o alerta foi gerado
    HoraLogin INT NOT NULL,                    -- Hora do login (0-23)
    DiaSemana INT NOT NULL,                    -- Dia da semana (0=Domingo, 6=Sábado)
    IpGrupo INT NULL,                          -- Grupo IP (parte inicial do IP)
    Score FLOAT NULL,                          -- Score bruto do modelo PCA
    ScorePercent FLOAT NULL,                   -- Score normalizado em percentual (0-100)
    NivelRisco VARCHAR(20) NULL,               -- Classificação: BAIXO, MÉDIO ou ALTO
    FonteDeteccao VARCHAR(50) NULL,            -- Fonte da detecção (ex: 'PCA', 'Hora', 'Ambos')
    Motivo VARCHAR(300) NULL,                  -- Descrição resumida do motivo do alerta
    Explicacao VARCHAR(500) NULL               -- Explicação detalhada (ex: “Maior desvio em IP | IP raro para este usuário”)
);


CREATE OR ALTER PROCEDURE dbo.Sp_ConsultarLoginsRecentes
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 5000
        usuarioid,
        datalogin,
        ip,
        navegador,
        fingerprint
    FROM LoginCliente
    ORDER BY datalogin DESC;
END;


CREATE TABLE LoginCliente (
    id INT IDENTITY(1,1) PRIMARY KEY, -- ID autoincrementável e chave primária
    usuarioid INT NOT NULL,           -- ID do usuário (assumindo que há uma tabela de usuários)
    datalogin DATETIME NOT NULL,      -- Data e hora do login
    ip VARCHAR(45) NOT NULL,          -- Endereço IP (IPv4 ou IPv6)
    navegador VARCHAR(255),           -- User Agent do navegador
    fingerprint VARCHAR(255)          -- Fingerprint do dispositivo/navegador (opcional, pode ser mais longo)
);

-- Primeiro, garanta que a tabela esteja vazia para cada execução de teste, se desejar.
-- CUIDADO: Isso irá apagar TODOS os dados existentes na tabela LoginCliente!
-- DELETE FROM LoginCliente;
-- DBCC CHECKIDENT ('LoginCliente', RESEED, 0); -- Reseta o contador IDENTITY, se a tabela estiver vazia

DECLARE @StartDate DATE = '2025-09-01';
DECLARE @EndDate DATE = '2025-09-10';
DECLARE @CurrentDate DATE;

-- Loop para cada dia no período especificado
SET @CurrentDate = @StartDate;
WHILE @CurrentDate <= @EndDate
BEGIN
    -- Base para adicionar horas e minutos. Convertemos @CurrentDate para DATETIME.
    DECLARE @BaseDateTime DATETIME = CAST(@CurrentDate AS DATETIME);

    -- Scenario 1: 3 clients log in only in the morning (with one anomaly for the first day)
    INSERT INTO LoginCliente (usuarioid, datalogin, ip, navegador, fingerprint) VALUES
    (101, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 360, DATEADD(hour, 6, @BaseDateTime)), '192.168.1.101', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.4896.88 Safari/537.36', 'fp_client101_v1'),
    (102, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 360, DATEADD(hour, 7, @BaseDateTime)), '192.168.1.102', 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.3 Safari/605.1.15', 'fp_client102_v1'),
    (103, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 360, DATEADD(hour, 8, @BaseDateTime)), '192.168.1.103', 'Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.4896.88 Mobile Safari/537.36', 'fp_client103_v1');

    -- Anomaly for Scenario 1 on the first day
    IF @CurrentDate = '2025-09-01'
    BEGIN
        INSERT INTO LoginCliente (usuarioid, datalogin, ip, navegador, fingerprint) VALUES
        (101, DATEADD(hour, 17, @BaseDateTime), '192.168.1.101', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.4896.88 Safari/537.36', 'fp_client101_anomaly');
    END

    -- Scenario 2: 3 clients log in from 8-9 and 18-19
    -- Morning login (8-9)
    INSERT INTO LoginCliente (usuarioid, datalogin, ip, navegador, fingerprint) VALUES
    (201, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 60, DATEADD(hour, 8, @BaseDateTime)), '192.168.2.201', 'Firefox/98.0', 'fp_client201_v1'),
    (202, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 60, DATEADD(hour, 8, @BaseDateTime)), '192.168.2.202', 'Edge/99.0', 'fp_client202_v1'),
    (203, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 60, DATEADD(hour, 8, @BaseDateTime)), '192.168.2.203', 'Opera/84.0', 'fp_client203_v1');

    -- Evening login (18-19)
    INSERT INTO LoginCliente (usuarioid, datalogin, ip, navegador, fingerprint) VALUES
    (201, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 60, DATEADD(hour, 18, @BaseDateTime)), '192.168.2.201', 'Firefox/98.0', 'fp_client201_v2'),
    (202, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 60, DATEADD(hour, 18, @BaseDateTime)), '192.168.2.202', 'Edge/99.0', 'fp_client202_v2'),
    (203, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 60, DATEADD(hour, 18, @BaseDateTime)), '192.168.2.203', 'Opera/84.0', 'fp_client203_v2');

    -- Scenario 3: 4 clients log in at random times
    INSERT INTO LoginCliente (usuarioid, datalogin, ip, navegador, fingerprint) VALUES
    (301, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 1440, @BaseDateTime), '192.168.3.301', 'Safari/15.3', 'fp_client301_v1'),
    (302, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 1440, @BaseDateTime), '192.168.3.302', 'Chrome/100.0', 'fp_client302_v1'),
    (303, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 1440, @BaseDateTime), '192.168.3.303', 'Firefox/98.0', 'fp_client303_v1'),
    (304, DATEADD(minute, ABS(CHECKSUM(NEWID())) % 1440, @BaseDateTime), '192.168.3.304', 'Edge/99.0', 'fp_client304_v1');

    -- Avança para o próximo dia
    SET @CurrentDate = DATEADD(day, 1, @CurrentDate);
END;

SELECT 'Dados de teste inseridos na tabela LoginCliente.' AS Mensagem;

-- Opcional: Visualize os dados inseridos
-- SELECT * FROM LoginCliente ORDER BY datalogin;