using System.Security.Cryptography;
using NexFiscal.Capture;

/// <summary>Reinstala A1 somente se o cofre estiver ausente, mantendo o cadastro e cursor existentes.</summary>
public sealed class CompanyCertificateRecovery(HanaSetup hana, CompanyCertificateVault vault)
{
    public async Task<object> RestoreAsync(string companyId, byte[] pfx, string password, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var id))
            throw new ArgumentException("Empresa inválida.");
        var key = id.ToString("D");
        if (vault.IsStored(key))
            throw new InvalidOperationException("Já existe A1 protegido para a empresa. Não é permitido sobrescrever.");
        var identity = A1CompanyCertificate.Inspect(pfx, password);
        var cs = hana.ConnectionString ?? throw new InvalidOperationException("HANA não configurado.");
        await using var connection = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        connection.ConnectionString = cs;
        await connection.OpenAsync(ct);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT \"Cnpj\" FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" WHERE \"CompanyId\"=?";
        var param = query.CreateParameter();
        param.Value = key;
        query.Parameters.Add(param);
        var expected = Convert.ToString(await query.ExecuteScalarAsync(ct))
            ?? throw new ArgumentException("Empresa não encontrada.");
        if (identity.Cnpj != expected)
            throw new ArgumentException("O certificado não pertence ao CNPJ cadastrado.");
        await vault.SaveAsync(key, expected, pfx, password, ct);
        return new { companyId = key, certificateStored = true };
    }
}
