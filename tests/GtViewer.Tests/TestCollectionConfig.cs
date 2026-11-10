using Xunit;

// Garante execução sequencial dos testes de integração para evitar colisões no banco PostgreSQL de testes
[assembly: CollectionBehavior(DisableTestParallelization = true)]
