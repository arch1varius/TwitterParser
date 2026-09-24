using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Parser.Infrastructure;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ParserDbContext>
{
    public ParserDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ParserDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Parser")
            ?? "Host=localhost;Database=twitter_parser;Username=parser").Options);
}
