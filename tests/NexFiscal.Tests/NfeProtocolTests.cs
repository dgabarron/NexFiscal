using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using NexFiscal.Web;
using static NexFiscal.Tests.Sefaz;

namespace NexFiscal.Tests;

/// <summary>Protocolo conferido contra o pacote oficial de schemas PL_NFeDistDFe_104 (Portal da NF-e, 03/07/2026).</summary>
public class NfeProtocolTests
{
    private static readonly string Schemas = Path.Combine(AppContext.BaseDirectory, "Schemas", "PL_NFeDistDFe_104");

    private static void ValidateAgainst(string schemaFile, XElement element)
    {
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        // O pacote de distribuição referencia o tipo DigestValueType da assinatura XML, que vem em outro pacote oficial.
        // Para estes testes basta declará-lo como base64, que é a sua definição no padrão XML-DSig.
        set.Add("http://www.w3.org/2000/09/xmldsig#", XmlReader.Create(new StringReader(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"http://www.w3.org/2000/09/xmldsig#\">" +
            "<xs:simpleType name=\"DigestValueType\"><xs:restriction base=\"xs:base64Binary\"/></xs:simpleType></xs:schema>")));
        set.Add("http://www.portalfiscal.inf.br/nfe", Path.Combine(Schemas, schemaFile));
        var errors = new List<string>();
        new XDocument(element).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    [Theory]
    [InlineData(TestCertificates.CnpjA, "SP", 0L)]
    [InlineData(TestCertificates.CnpjA, null, 123456789012345L)]
    [InlineData("12ABC34501DE35", "RJ", 42L)] // CNPJ alfanumérico (NT 2014.002 v1.40)
    [InlineData(TestCertificates.CnpjA, "XX", 7L)] // UF fora da tabela do IBGE é omitida
    public void Pedidos_distNSU_e_consNSU_sao_validos_no_schema_oficial(string cnpj, string? uf, long nsu)
    {
        foreach (var envelope in new[] { NfeProtocol.BuildDistributionRequest(cnpj, uf, nsu), NfeProtocol.BuildNsuQueryRequest(cnpj, uf, nsu) })
        {
            var data = NfeProtocol.DataOf(envelope);
            ValidateAgainst("distDFeInt_v1.01.xsd", data);
            Assert.Equal("1.01", (string?)data.Attribute("versao"));
            Assert.Equal("1", data.Elements().First(e => e.Name.LocalName == "tpAmb").Value);
            Assert.Equal(uf is "SP" or "RJ", data.Elements().Any(e => e.Name.LocalName == "cUFAutor"));
            Assert.Contains(nsu.ToString("D15"), envelope);
            // Regra 404 da NT: sem prefixo de namespace na área de dados; regra 214: mensagem até 10 KB.
            Assert.DoesNotMatch("<\\w+:(distDFeInt|tpAmb|CNPJ|distNSU|consNSU)", envelope);
            Assert.True(Encoding.UTF8.GetByteCount(envelope) < 10_000);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", envelope);
            Assert.Contains("http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe", envelope);
        }
    }

    [Fact]
    public void Pedido_com_cnpj_ou_nsu_invalido_nem_e_montado()
    {
        Assert.Throws<ArgumentException>(() => NfeProtocol.BuildDistributionRequest("11222333000182", "SP", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfeProtocol.BuildDistributionRequest(TestCertificates.CnpjA, "SP", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfeProtocol.BuildNsuQueryRequest(TestCertificates.CnpjA, "SP", 1_000_000_000_000_000));
    }

    [Fact]
    public void Resumos_usados_nos_testes_seguem_o_schema_oficial()
    {
        ValidateAgainst("resNFe_v1.01.xsd", XElement.Parse(ResNFe(Key(1))));
        ValidateAgainst("resEvento_v1.01.xsd", XElement.Parse(CancelEvent(Key(1))));
    }

    [Fact]
    public void Resposta_138_e_lida_com_documentos_descompactados_e_classificados()
    {
        var response = NfeProtocol.ParseResponse(Found(4, 9, Complete(1, 101), Summary(2, 102), Cancel(3, 103),
            new Doc(null, "procEventoNFe_v1.00.xsd", "<procEventoNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"/>")));

        Assert.Equal((138, 4L, 9L), (response.Status, response.LastNsu, response.MaxNsu));
        Assert.Equal([NfeDocumentKind.Complete, NfeDocumentKind.Summary, NfeDocumentKind.Event, NfeDocumentKind.Event], response.Documents.Select(d => d.Kind));
        Assert.Equal([1L, 2L, 3L, null], response.Documents.Select(d => d.Nsu)); // NSU do docZip é opcional desde a v1.15
        Assert.Equal(ProcNFe(Key(101)), Encoding.UTF8.GetString(response.Documents[0].Xml));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(response.Documents[0].Xml)), response.Documents[0].Sha256);

        var complete = NfeProtocol.ReadDocument(NfeDocumentKind.Complete, response.Documents[0].Xml)!;
        Assert.Equal((Key(101), true, IssuerCnpj, "FORNECEDOR EXEMPLO LTDA", 1234.56m), (complete.AccessKey, complete.Complete, complete.IssuerId, complete.IssuerName, complete.TotalValue));
        Assert.Equal(new DateTime(2026, 11, 1, 13, 0, 0, DateTimeKind.Utc), complete.IssuedAtUtc);
        var summary = NfeProtocol.ReadDocument(NfeDocumentKind.Summary, response.Documents[1].Xml)!;
        Assert.Equal((Key(102), false, 1), (summary.AccessKey, summary.Complete, summary.Situation));
        Assert.Equal(Key(103), NfeProtocol.CancelledKeyOf(response.Documents[2].Xml));
        Assert.Null(NfeProtocol.CancelledKeyOf(response.Documents[1].Xml));
    }

    [Fact]
    public void Exemplos_de_retorno_da_nota_tecnica_sao_lidos_inclusive_em_soap_1_1()
    {
        // Exemplo 2 do item 6 da NT 2014.002 v1.40 (cStat 137), com envelope SOAP 1.1.
        const string example =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soap:Envelope xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><nfeDistDFeInteresseResponse xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe\">" +
            "<nfeDistDFeInteresseResult><retDistDFeInt xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.01\"><tpAmb>2</tpAmb><verAplic>1.4.0</verAplic><cStat>137</cStat><xMotivo>Nenhum documento localizado</xMotivo>" +
            "<dhResp>2022-04-04T11:54:49-03:00</dhResp><ultNSU>000000000000000</ultNSU><maxNSU>000000000000000</maxNSU></retDistDFeInt>" +
            "</nfeDistDFeInteresseResult></nfeDistDFeInteresseResponse></soap:Body></soap:Envelope>";
        var response = NfeProtocol.ParseResponse(example);
        Assert.Equal((137, "Nenhum documento localizado", 0L, 0L), (response.Status, response.Reason, response.LastNsu, response.MaxNsu));
        Assert.Empty(response.Documents);

        var rejection = NfeProtocol.ParseResponse(Response(656, "Rejeicao: Consumo Indevido", null, null));
        Assert.Equal((656, (long?)null, (long?)null), (rejection.Status, rejection.LastNsu, rejection.MaxNsu));
    }

    public static TheoryData<string, string> BadResponses() => new()
    {
        { "isto não é xml", "sefaz_invalid_response" },
        { "<a/>", "sefaz_invalid_response" },
        { "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body><soap:Fault/></soap:Body></soap:Envelope>", "sefaz_fault" },
        { Found(1, 1, Complete(1, 1)).Replace("<cStat>138</cStat>", "<cStat>abc</cStat>"), "sefaz_invalid_response" },
        { Found(1, 1, Complete(1, 1)).Replace(Zip(ProcNFe(Key(1))), "@@nao-base64@@"), "sefaz_invalid_response" },
        { Found(1, 1, Complete(1, 1)).Replace(Zip(ProcNFe(Key(1))), Convert.ToBase64String("não é gzip"u8.ToArray())), "sefaz_invalid_response" },
        { Found(51, 51, Enumerable.Range(1, 51).Select(i => Complete(i, i)).ToArray()), "sefaz_invalid_response" },
        { "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>" + Found(1, 1), "sefaz_invalid_response" },
    };

    [Theory]
    [MemberData(nameof(BadResponses))]
    public void Resposta_fora_do_padrao_e_recusada_sem_ser_tratada_como_lote(string soap, string kind)
    {
        var error = Assert.Throws<NexFiscalException>(() => NfeProtocol.ParseResponse(soap));
        Assert.Equal(kind, error.Kind);
    }

    [Theory]
    [InlineData("<nfeProc xmlns=\"http://www.portalfiscal.inf.br/nfe\"/>")]
    [InlineData("<resNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"><chNFe>123</chNFe></resNFe>")]
    [InlineData("<nfeProc>sem namespace</nfeProc>")]
    [InlineData("xml quebrado <")]
    public void Documento_incompleto_nao_e_reconhecido_como_xml_completo(string xml)
    {
        Assert.Null(NfeProtocol.ReadDocument(NfeDocumentKind.Complete, Encoding.UTF8.GetBytes(xml)));
        Assert.Null(NfeProtocol.ReadDocument(NfeDocumentKind.Summary, Encoding.UTF8.GetBytes(xml)));
        // Um resumo válido nunca é aceito como XML completo.
        Assert.Null(NfeProtocol.ReadDocument(NfeDocumentKind.Complete, Encoding.UTF8.GetBytes(ResNFe(Key(1)))));
    }

    [Fact]
    public void Pasta_de_xml_so_aceita_chave_valida_como_nome_de_arquivo()
    {
        Assert.Throws<ArgumentException>(() => NfeXmlFolder.PathFor(Path.GetTempPath(), "..\\..\\windows\\system32"));
        Assert.Throws<ArgumentException>(() => NfeXmlFolder.PathFor(Path.GetTempPath(), Key(1) + "/x"));
        Assert.EndsWith(Key(1) + ".xml", NfeXmlFolder.PathFor(Path.GetTempPath(), Key(1)));
    }

    // ---------- transporte HTTPS com certificado do cliente (servidor local real) ----------

    private static async Task<(WebApplication App, Uri Url, X509Certificate2 ServerCertificate)> StartServerAsync(RequestDelegate handler)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var server = new X509Certificate2(created.Export(X509ContentType.Pfx, "x"), "x");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = server;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.AllowAnyClientCertificate();
        })));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return (app, new Uri(app.Urls.Single()), server);
    }

    [Fact]
    public async Task Transporte_autentica_com_o_a1_guardado_e_envia_soap_1_2_com_a_action_oficial()
    {
        using var install = await CaptureInstallation.CreateAsync();
        string? contentType = null, body = null, clientThumbprint = null;
        var (app, url, serverCertificate) = await StartServerAsync(async context =>
        {
            contentType = context.Request.ContentType;
            clientThumbprint = context.Connection.ClientCertificate?.GetCertHashString(HashAlgorithmName.SHA256);
            body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            context.Response.ContentType = "application/soap+xml; charset=utf-8";
            await context.Response.WriteAsync(None(7));
        });
        await using (app)
        {
            var transport = new HttpSefazTransport { TrustedTestServerCertificate = serverCertificate };
            using var certificate = install.Companies.OpenCertificate(install.CompanyId); // mesmo caminho usado na captura real
            var envelope = NfeProtocol.BuildDistributionRequest(TestCertificates.CnpjA, "SP", 7);

            var response = NfeProtocol.ParseResponse(await transport.SendAsync(url, envelope, certificate, CancellationToken.None));

            Assert.Equal((137, 7L), (response.Status, response.LastNsu));
            Assert.Equal(envelope, body);
            Assert.StartsWith("application/soap+xml", contentType);
            Assert.Contains("action=\"" + NfeProtocol.SoapAction + "\"", contentType);
            Assert.Equal(install.Base.Store.Rows[0].Thumbprint, clientThumbprint);
        }
    }

    [Fact]
    public async Task Transporte_nao_confia_em_servidor_com_certificado_desconhecido()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var (app, url, _) = await StartServerAsync(context => context.Response.WriteAsync(None(0)));
        await using (app)
        {
            using var certificate = install.Companies.OpenCertificate(install.CompanyId);
            var error = await Assert.ThrowsAsync<NexFiscalException>(() =>
                new HttpSefazTransport().SendAsync(url, "<x/>", certificate, CancellationToken.None));
            Assert.Equal("sefaz_tls", error.Kind);
        }
    }

    [Theory]
    [InlineData(403, "sefaz_certificate_rejected")]
    [InlineData(500, "sefaz_http_error")]
    public async Task Erros_http_da_sefaz_viram_mensagens_especificas(int status, string kind)
    {
        using var install = await CaptureInstallation.CreateAsync();
        var (app, url, serverCertificate) = await StartServerAsync(context => { context.Response.StatusCode = status; return Task.CompletedTask; });
        await using (app)
        {
            using var certificate = install.Companies.OpenCertificate(install.CompanyId);
            var transport = new HttpSefazTransport { TrustedTestServerCertificate = serverCertificate };
            var error = await Assert.ThrowsAsync<NexFiscalException>(() => transport.SendAsync(url, "<x/>", certificate, CancellationToken.None));
            Assert.Equal(kind, error.Kind);
        }
    }

    [Fact]
    public async Task Servidor_fora_do_ar_gera_erro_de_conexao_e_nao_excecao_crua()
    {
        using var install = await CaptureInstallation.CreateAsync();
        using var certificate = install.Companies.OpenCertificate(install.CompanyId);
        var error = await Assert.ThrowsAsync<NexFiscalException>(() =>
            new HttpSefazTransport().SendAsync(new Uri("https://127.0.0.1:1/"), "<x/>", certificate, CancellationToken.None));
        Assert.Equal("sefaz_unreachable", error.Kind);
    }
}
