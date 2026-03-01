var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Scrinia_Server>("scrinia-server");

builder.Build().Run();
