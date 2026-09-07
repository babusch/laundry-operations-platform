using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Persistence;

// Connectivity only for now. Add entities and explicit migrations at the acceptance checkpoint.
public sealed class PlantDbContext(DbContextOptions<PlantDbContext> options) : DbContext(options);
