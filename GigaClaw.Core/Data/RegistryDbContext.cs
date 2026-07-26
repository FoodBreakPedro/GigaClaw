using Microsoft.EntityFrameworkCore;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Data;

public class RegistryDbContext : DbContext
{
    public DbSet<Project> Projects => Set<Project>();

    private readonly string _dbPath;

    public RegistryDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}");
    }
}
