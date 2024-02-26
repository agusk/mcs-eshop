using eShop.AppHost;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddForwardedHeaders();

var redis = builder.AddRedisContainer("redis");
var rabbitMq = builder.AddRabbitMQContainer("eventbus")
    // .WithAnnotation(new EndpointAnnotation(ProtocolType.Tcp, port: 8080, containerPort: 15672)) 
    .WithAnnotation(new ContainerImageAnnotation
    {
        Image = "rabbitmq",
        Tag = "3-management"
    })        
    // .WithAnnotation(new EndpointAnnotation(
    //     ProtocolType.Tcp, 
    //     uriScheme: "http", 
    //     name: "management", 
    //     port: 8080, 
    //     containerPort: 15672))
    .WithEndpoint(containerPort: 15672, hostPort: 8080, name: "management", scheme: "http")
    // .WithHttpEndpoint(containerPort: 15672, hostPort: 8080, name: "management")
    .WithEnvironment("RABBITMQ_DEFAULT_USER", "guest")
    .WithEnvironment("RABBITMQ_DEFAULT_PASS", "guest")
    .WithVolumeMount("./rabbitmq", "/var/lib/rabbitmq/mnesia/");
    // .PublishAsContainer();


var postgres = builder.AddPostgresContainer("postgres",5432,"pass123")
    .WithAnnotation(new ContainerImageAnnotation
    {
        Image = "ankane/pgvector",
        Tag = "latest"
    })    
    .WithVolumeMount("./postgres", "/var/lib/postgresql/data");
    

var pgAdmin = builder.AddContainer("pgadmin","dpage/pgadmin4","latest")
    .WithHttpEndpoint(containerPort: 80, hostPort: 5000, name: "pgadmin")
    .WithVolumeMount("./pgadmin", "/var/lib/pgadmin")
    .WithEnvironment("PGADMIN_DEFAULT_EMAIL", "a@email.com")
    .WithEnvironment("PGADMIN_DEFAULT_PASSWORD", "pass123")
    .ExcludeFromManifest();



var catalogDb = postgres.AddDatabase("catalogdb");
var identityDb = postgres.AddDatabase("identitydb");
var orderDb = postgres.AddDatabase("orderingdb");
var webhooksDb = postgres.AddDatabase("webhooksdb");

var openAi = builder.AddAzureOpenAI("openai");

// Services
var identityApi = builder.AddProject<Projects.Identity_API>("identity-api")
    .WithReference(identityDb)
    .WithLaunchProfile("https");

var idpHttps = identityApi.GetEndpoint("https");

var basketApi = builder.AddProject<Projects.Basket_API>("basket-api")
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithEnvironment("Identity__Url", idpHttps);

var catalogApi = builder.AddProject<Projects.Catalog_API>("catalog-api")
    .WithReference(rabbitMq)
    .WithReference(catalogDb)
    .WithReference(openAi, optional: true);

var orderingApi = builder.AddProject<Projects.Ordering_API>("ordering-api")
    .WithReference(rabbitMq)
    .WithReference(orderDb)
    .WithEnvironment("Identity__Url", idpHttps);

builder.AddProject<Projects.OrderProcessor>("order-processor")
    .WithReference(rabbitMq)
    .WithReference(orderDb);

builder.AddProject<Projects.PaymentProcessor>("payment-processor")
    .WithReference(rabbitMq);

var webHooksApi = builder.AddProject<Projects.Webhooks_API>("webhooks-api")
    .WithReference(rabbitMq)
    .WithReference(webhooksDb)
    .WithEnvironment("Identity__Url", idpHttps);

// Reverse proxies
builder.AddProject<Projects.Mobile_Bff_Shopping>("mobile-bff")
    .WithReference(catalogApi)
    .WithReference(identityApi);

// Apps
var webhooksClient = builder.AddProject<Projects.WebhookClient>("webhooksclient")
    .WithReference(webHooksApi)
    .WithEnvironment("IdentityUrl", idpHttps);

var webApp = builder.AddProject<Projects.WebApp>("webapp")
    .WithReference(basketApi)
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WithReference(rabbitMq)
    .WithReference(openAi, optional: true)
    .WithEnvironment("IdentityUrl", idpHttps)
    .WithLaunchProfile("https");

// Wire up the callback urls (self referencing)
webApp.WithEnvironment("CallBackUrl", webApp.GetEndpoint("https"));
webhooksClient.WithEnvironment("CallBackUrl", webhooksClient.GetEndpoint("https"));

// Identity has a reference to all of the apps for callback urls, this is a cyclic reference
identityApi.WithEnvironment("BasketApiClient", basketApi.GetEndpoint("http"))
           .WithEnvironment("OrderingApiClient", orderingApi.GetEndpoint("http"))
           .WithEnvironment("WebhooksApiClient", webHooksApi.GetEndpoint("http"))
           .WithEnvironment("WebhooksWebClient", webhooksClient.GetEndpoint("https"))
           .WithEnvironment("WebAppClient", webApp.GetEndpoint("https"));

builder.Build().Run();
