using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GtViewer.Tests;

public class HomePageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HomePageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_HomePage_ReturnsSuccessAndCorrectContentType()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType.ToString());

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("gt_viewer", content);
        Assert.Contains("Gerenciamento de Computadores Windows", content);
    }
}
