var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Scrinia_Server>("scrinium");

builder.Build().Run();
