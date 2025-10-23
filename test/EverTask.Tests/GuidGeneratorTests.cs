using UUIDNext;

namespace EverTask.Tests;

public class GuidGeneratorTests
{
    [Fact]
    public void DefaultGuidGenerator_ShouldGenerateValidGuid()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.Other);

        // Act
        var guid = generator.NewDatabaseFriendly();

        // Assert
        guid.ShouldNotBe(Guid.Empty);
        guid.ShouldBeOfType<Guid>();
    }

    [Fact]
    public void DefaultGuidGenerator_ShouldGenerateUniqueGuids()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.Other);

        // Act
        var guid1 = generator.NewDatabaseFriendly();
        var guid2 = generator.NewDatabaseFriendly();
        var guid3 = generator.NewDatabaseFriendly();

        // Assert
        guid1.ShouldNotBe(guid2);
        guid2.ShouldNotBe(guid3);
        guid1.ShouldNotBe(guid3);
    }

    [Fact]
    public void DefaultGuidGenerator_WithSqlServer_ShouldGenerateValidGuid()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.SqlServer);

        // Act
        var guid = generator.NewDatabaseFriendly();

        // Assert
        guid.ShouldNotBe(Guid.Empty);
        guid.ShouldBeOfType<Guid>();
    }

    [Fact]
    public void DefaultGuidGenerator_WithSQLite_ShouldGenerateValidGuid()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.SQLite);

        // Act
        var guid = generator.NewDatabaseFriendly();

        // Assert
        guid.ShouldNotBe(Guid.Empty);
        guid.ShouldBeOfType<Guid>();
    }

    [Fact]
    public void DefaultGuidGenerator_WithPostgreSql_ShouldGenerateValidGuid()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.PostgreSql);

        // Act
        var guid = generator.NewDatabaseFriendly();

        // Assert
        guid.ShouldNotBe(Guid.Empty);
        guid.ShouldBeOfType<Guid>();
    }

    [Fact]
    public void DefaultGuidGenerator_ShouldGenerateSequentialGuids()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.Other);

        // Act - Generate multiple GUIDs with small delay
        var guids = new List<Guid>();
        for (int i = 0; i < 100; i++)
        {
            guids.Add(generator.NewDatabaseFriendly());
        }

        // Assert - All should be unique
        var uniqueGuids = guids.Distinct().ToList();
        uniqueGuids.Count.ShouldBe(100);
    }

    [Theory]
    [InlineData(Database.Other)]
    [InlineData(Database.SqlServer)]
    [InlineData(Database.SQLite)]
    [InlineData(Database.PostgreSql)]
    public void DefaultGuidGenerator_WithDifferentDatabases_ShouldGenerateValidGuids(Database database)
    {
        // Arrange
        var generator = new DefaultGuidGenerator(database);

        // Act
        var guid = generator.NewDatabaseFriendly();

        // Assert
        guid.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void DefaultGuidGenerator_MultipleInstances_ShouldGenerateUniqueGuids()
    {
        // Arrange
        var generator1 = new DefaultGuidGenerator(Database.Other);
        var generator2 = new DefaultGuidGenerator(Database.SqlServer);
        var generator3 = new DefaultGuidGenerator(Database.SQLite);

        // Act
        var guid1 = generator1.NewDatabaseFriendly();
        var guid2 = generator2.NewDatabaseFriendly();
        var guid3 = generator3.NewDatabaseFriendly();

        // Assert - Different generators should produce different GUIDs
        guid1.ShouldNotBe(guid2);
        guid2.ShouldNotBe(guid3);
        guid1.ShouldNotBe(guid3);
    }

    [Fact]
    public void DefaultGuidGenerator_HighVolumeGeneration_ShouldNotProduceDuplicates()
    {
        // Arrange
        var generator = new DefaultGuidGenerator(Database.SqlServer);
        var guidSet = new HashSet<Guid>();
        const int iterations = 10000;

        // Act
        for (int i = 0; i < iterations; i++)
        {
            var guid = generator.NewDatabaseFriendly();
            guidSet.Add(guid);
        }

        // Assert - All GUIDs should be unique
        guidSet.Count.ShouldBe(iterations);
    }
}
