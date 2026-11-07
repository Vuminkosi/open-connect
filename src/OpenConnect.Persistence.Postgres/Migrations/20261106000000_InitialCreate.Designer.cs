using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.Postgres.Migrations;

[DbContext(typeof(OpenConnectDbContext))]
[Migration("20261106000000_InitialCreate")]
partial class InitialCreate
{
}
