using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using NexFiscal.Capture;

/// <summary>
/// A1 armazenado cifrado fora de wwwroot. Sem senha nem PFX em claro no HANA.
/// Proteção at rest via ASP.NET Data Protection + DPAPI do usuário Windows.
/// Arquivos identificados somente por CompanyId gerado pelo servidor.
/// </summary>
public sealed class CompanyCertificateVault(IDataProtectionProvider keys, NexFiscalDataDirectory dataDirectory)
{
    private readonly IDataProtector _protector = keys.CreateProtector("NexFiscal.CompanyA1.v1");
    private string VaultPath => dataDirectory.PathFor(".nexfiscal-certificates");

    private string PathFor(string companyId)
    {
        if (!Guid.TryParse(companyId, out var id))
            throw new ArgumentException("Identificador de empresa inválido.");
        return Path.Combine(VaultPath, id.ToString("D") + ".a1enc");
    }

    public async Task SaveAsync(string companyId, string expectedCnpj, byte[] pfx, string password, CancellationToken ct)
    {
        var identity = A1CompanyCertificate.Inspect(pfx, password);
        if (!string.Equals(identity.Cnpj, expectedCnpj, StringComparison.Ordinal))
            throw new InvalidDataException("Certificado não corresponde ao CNPJ da empresa.");

        // Payload binário versionado: 1 byte versão, 4 bytes tamanho PFX,
        // PFX, 4 bytes tamanho senha UTF-8, senha. Só existe em RAM.
        var pass = Encoding.UTF8.GetBytes(password);
        var payload = new byte[1 + 4 + pfx.Length + 4 + pass.Length];
        payload[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), pfx.Length);
        pfx.CopyTo(payload, 5);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5 + pfx.Length, 4), pass.Length);
        pass.CopyTo(payload, 9 + pfx.Length);

        try
        {
            var encrypted = _protector.Protect(payload);
            Directory.CreateDirectory(VaultPath);
            var path = PathFor(companyId);
            if (File.Exists(path))
                throw new InvalidOperationException("Certificado já armazenado: substituição exige fluxo próprio.");
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await file.WriteAsync(encrypted, ct);
                // Sem overwrite acidental de certificado anterior.
                File.Move(tempPath, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(pass);
        }
    }

    public async Task<(byte[] Pfx, string Password)> ReadAsync(string companyId, CancellationToken ct)
    {
        var encrypted = await File.ReadAllBytesAsync(PathFor(companyId), ct);
        byte[] payload;
        try { payload = _protector.Unprotect(encrypted); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
        try
        {
            if (payload.Length < 9 || payload[0] != 1)
                throw new InvalidDataException("Formato de certificado não suportado.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1, 4));
            if (length <= 0 || length > 5_000_000 || payload.Length < 9 + length)
                throw new InvalidDataException("Certificado protegido inválido.");
            int passLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5 + length, 4));
            if (passLength <= 0 || passLength > 4096 || payload.Length != 9 + length + passLength)
                throw new InvalidDataException("Credencial protegida inválida.");
            return (payload.AsSpan(5, length).ToArray(), Encoding.UTF8.GetString(payload.AsSpan(9 + length, passLength)));
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public bool IsStored(string companyId) => File.Exists(PathFor(companyId));

    // Presença em disco não significa que o A1 pode ser aberto com as chaves
    // Data Protection e o usuário Windows da instalação atual.
    public string StorageStatus(string companyId)
    {
        var path = PathFor(companyId);
        if (!File.Exists(path)) return "missing";
        byte[]? encrypted = null;
        byte[]? clear = null;
        try
        {
            encrypted = File.ReadAllBytes(path);
            clear = _protector.Unprotect(encrypted);
            if (clear.Length < 9 || clear[0] != 1) return "unreadable";
            var size = BinaryPrimitives.ReadInt32LittleEndian(clear.AsSpan(1, 4));
            if (size is <= 0 or > 5_000_000 || clear.Length < 9 + size)
                return "unreadable";
            var passwordSize = BinaryPrimitives.ReadInt32LittleEndian(clear.AsSpan(5 + size, 4));
            if (passwordSize <= 0 || passwordSize > 4096 ||
                clear.Length != 9 + size + passwordSize) return "unreadable";
            return "stored";
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            return "unreadable";
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    // Limpeza autorizada somente após exclusão de cadastro sem histórico fiscal.
    public void DeleteUnused(string companyId)
    {
        var path = PathFor(companyId);
        if (File.Exists(path)) File.Delete(path);
    }
}
