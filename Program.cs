using System.Text;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var arquivoDb = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Banco:Arquivo"] ?? "estoque.db");
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={arquivoDb};Default Timeout=30"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

// Senha de acesso (Basic Auth). Deixe "Senha" vazia no appsettings.json para desativar.
var senha = app.Configuration["Acesso:Senha"];
if (!string.IsNullOrEmpty(senha))
{
    var usuario = app.Configuration["Acesso:Usuario"] ?? "admin";
    var esperado = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{usuario}:{senha}"));
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.Authorization == esperado) { await next(); return; }
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Estoque\"";
        ctx.Response.StatusCode = 401;
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

static DateTime Agora()
{
    try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")); }
    catch { return DateTime.Now; }
}

static IResult Erro(string msg) => Results.BadRequest(new { erro = msg });

var api = app.MapGroup("/api");

// ---------- Produtos ----------

api.MapGet("/produtos", async (AppDb db) => await db.Produtos.OrderBy(p => p.Nome).ToListAsync());

api.MapPost("/produtos", async (ProdutoDto d, AppDb db) =>
{
    var sku = (d.Sku ?? "").Trim().ToUpper();
    if (sku == "" || string.IsNullOrWhiteSpace(d.Nome)) return Erro("Preencha o SKU e o nome.");
    if (d.Custo < 0 || d.Preco < 0 || d.Quantidade < 0 || d.EstoqueMinimo < 0) return Erro("Valores não podem ser negativos.");
    if (await db.Produtos.AnyAsync(x => x.Sku == sku)) return Erro("Já existe um produto com esse SKU.");

    var novo = new Produto
    {
        Sku = sku, Nome = d.Nome.Trim(), Custo = d.Custo, Preco = d.Preco,
        Quantidade = d.Quantidade, EstoqueMinimo = d.EstoqueMinimo
    };
    db.Produtos.Add(novo);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException) { return Erro("Já existe um produto com esse SKU."); }
    return Results.Ok(novo);
});

// Edita apenas dados cadastrais; a quantidade só muda por venda/ajuste (evita sobrescrever o trabalho de outra pessoa).
api.MapPut("/produtos/{id:int}", async (int id, ProdutoDto d, AppDb db) =>
{
    var p = await db.Produtos.FindAsync(id);
    if (p is null) return Results.NotFound(new { erro = "Produto não encontrado." });
    if (string.IsNullOrWhiteSpace(d.Nome)) return Erro("Preencha o nome.");
    if (d.Custo < 0 || d.Preco < 0 || d.EstoqueMinimo < 0) return Erro("Valores não podem ser negativos.");
    p.Nome = d.Nome.Trim(); p.Custo = d.Custo; p.Preco = d.Preco; p.EstoqueMinimo = d.EstoqueMinimo;
    await db.SaveChangesAsync();
    return Results.Ok(p);
});

// Entrada (+) ou baixa/correção (-) de estoque, de forma atômica.
api.MapPost("/produtos/{id:int}/ajuste", async (int id, AjusteDto d, AppDb db) =>
{
    if (d.Quantidade == 0 && d.NovoCusto <= 0) return Erro("Informe a quantidade ou o novo custo.");
    var n = await db.Produtos.Where(p => p.Id == id && p.Quantidade + d.Quantidade >= 0)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Quantidade, p => p.Quantidade + d.Quantidade));
    if (n == 0) return Erro("Produto não encontrado ou o estoque ficaria negativo.");
    if (d.NovoCusto > 0)
        await db.Produtos.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Custo, d.NovoCusto));
    return Results.NoContent();
});

api.MapDelete("/produtos/{id:int}", async (int id, AppDb db) =>
{
    await db.Produtos.Where(p => p.Id == id).ExecuteDeleteAsync();
    return Results.NoContent();
});

// ---------- Vendas ----------

api.MapGet("/vendas", async (AppDb db) => await db.Vendas.OrderByDescending(v => v.Data).ToListAsync());

api.MapPost("/vendas", async (VendaDto d, AppDb db) =>
{
    if (d.Quantidade <= 0 || d.PrecoUnit <= 0) return Erro("Quantidade e preço devem ser maiores que zero.");
    if (d.TaxaPercent < 0 || d.TaxaFixa < 0 || d.Frete < 0) return Erro("Taxas e frete não podem ser negativos.");

    await using var tx = await db.Database.BeginTransactionAsync();

    // Baixa atômica: só desconta se ainda houver estoque. Duas vendas simultâneas nunca deixam o saldo negativo.
    var baixou = await db.Produtos.Where(p => p.Id == d.ProdutoId && p.Quantidade >= d.Quantidade)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Quantidade, p => p.Quantidade - d.Quantidade));
    if (baixou == 0)
    {
        var existe = await db.Produtos.AnyAsync(p => p.Id == d.ProdutoId);
        return existe ? Erro("Estoque insuficiente.") : Results.NotFound(new { erro = "Produto não encontrado." });
    }

    var prod = await db.Produtos.AsNoTracking().FirstAsync(p => p.Id == d.ProdutoId);
    var venda = new Venda
    {
        Data = Agora(), Sku = prod.Sku, Nome = prod.Nome, Quantidade = d.Quantidade,
        PrecoUnit = d.PrecoUnit, CustoUnit = prod.Custo,
        TaxaPercent = d.TaxaPercent, TaxaFixa = d.TaxaFixa, Frete = d.Frete
    };
    db.Vendas.Add(venda);
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.Ok(new { venda, estoqueBaixo = prod.Quantidade <= prod.EstoqueMinimo, restante = prod.Quantidade });
});

// Cancela a venda e devolve os itens ao estoque.
api.MapDelete("/vendas/{id:int}", async (int id, AppDb db) =>
{
    var v = await db.Vendas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (v is null) return Results.NotFound(new { erro = "Venda não encontrada." });

    await using var tx = await db.Database.BeginTransactionAsync();
    var apagou = await db.Vendas.Where(x => x.Id == id).ExecuteDeleteAsync();
    if (apagou == 0) return Results.NoContent();
    await db.Produtos.Where(p => p.Sku == v.Sku)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Quantidade, p => p.Quantidade + v.Quantidade));
    await tx.CommitAsync();
    return Results.NoContent();
});

app.Run();

// ---------- Modelos ----------

record ProdutoDto(string? Sku, string? Nome, decimal Custo, decimal Preco, int Quantidade, int EstoqueMinimo);
record AjusteDto(int Quantidade, decimal NovoCusto);
record VendaDto(int ProdutoId, int Quantidade, decimal PrecoUnit, decimal TaxaPercent, decimal TaxaFixa, decimal Frete);

class Produto
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Nome { get; set; } = "";
    public decimal Custo { get; set; }
    public decimal Preco { get; set; }
    public int Quantidade { get; set; }
    public int EstoqueMinimo { get; set; }

    public bool EstoqueBaixo => Quantidade <= EstoqueMinimo;
}

class Venda
{
    public int Id { get; set; }
    public DateTime Data { get; set; }
    public string Sku { get; set; } = "";
    public string Nome { get; set; } = "";
    public int Quantidade { get; set; }
    public decimal PrecoUnit { get; set; }
    public decimal CustoUnit { get; set; }
    public decimal TaxaPercent { get; set; }
    public decimal TaxaFixa { get; set; }
    public decimal Frete { get; set; }

    public decimal Receita => PrecoUnit * Quantidade;
    public decimal CustoTotal => CustoUnit * Quantidade;
    public decimal Despesas => Receita * TaxaPercent / 100 + TaxaFixa * Quantidade + Frete;
    public decimal Lucro => Receita - CustoTotal - Despesas;
}

class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }
    public DbSet<Produto> Produtos => Set<Produto>();
    public DbSet<Venda> Vendas => Set<Venda>();

    protected override void OnModelCreating(ModelBuilder mb) =>
        mb.Entity<Produto>().HasIndex(p => p.Sku).IsUnique();
}
