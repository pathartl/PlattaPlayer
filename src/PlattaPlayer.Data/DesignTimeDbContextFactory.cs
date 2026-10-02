using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlattaPlayer.Data;

/// <summary>Used by the EF Core CLI (`dotnet ef`) to construct the context at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    public LibraryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(AppPaths.SqliteConnectionString)
            .Options;
        return new LibraryDbContext(options);
    }
}
