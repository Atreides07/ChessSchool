using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Tests;

/// <summary>
/// Демо-посев школьного домена. Ключевое свойство — идемпотентность ПО ШАГАМ: восстанавливает
/// недостающее (в т.ч. учеников, пропавших при пересоздании БД), не плодит дубликаты при повторе.
/// </summary>
public class SeedDataTests
{
    private static SchoolDbContext NewDb()
    {
        var db = new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public void Ensure_OnEmptyDb_SeedsFullSchoolDomain()
    {
        using var db = NewDb();

        SeedData.Ensure(db);

        Assert.True(db.Schools.Any(s => s.Id == SeedData.SchoolId && s.OwnerSub == Demo.OwnerSub));
        Assert.True(db.Groups.Any(g => g.Id == SeedData.GroupId));
        Assert.Equal(6, db.Students.Count(s => s.GroupId == SeedData.GroupId));
        Assert.Equal(3, db.Devices.Count(d => d.SchoolId == SeedData.SchoolId));
        Assert.True(db.RatingPoints.Any());          // история рейтинга посеяна
        Assert.True(db.Games.Any());                 // партии (сыгранные + очередь атрибуции)
        Assert.True(db.Games.Any(g => g.Source == AttributionSource.None)); // очередь тренера
        Assert.True(db.ShareLinks.Any(l => l.Token == SeedData.ParentShareToken)); // ссылка родителю
    }

    [Fact]
    public void Ensure_WhenSchoolExistsButStudentsMissing_RepopulatesStudents()
    {
        // Регрессия на баг «после разделения БД исчезли ученики»: строка школы уцелела, ученики пропали.
        // Старый ранний return при db.Schools.Any() их бы не вернул.
        using var db = NewDb();
        db.Schools.Add(new School { Id = SeedData.SchoolId, Name = "Шахматная школа №1", OwnerSub = Demo.OwnerSub });
        db.SaveChanges();
        Assert.Empty(db.Students);

        SeedData.Ensure(db);

        Assert.Equal(6, db.Students.Count(s => s.GroupId == SeedData.GroupId));
    }

    [Fact]
    public void Ensure_BackfillsOwnerSub_WhenNull()
    {
        using var db = NewDb();
        db.Schools.Add(new School { Id = SeedData.SchoolId, Name = "Шахматная школа №1", OwnerSub = null });
        db.SaveChanges();

        SeedData.Ensure(db);

        Assert.Equal(Demo.OwnerSub, db.Schools.Single(s => s.Id == SeedData.SchoolId).OwnerSub);
    }

    [Fact]
    public void Ensure_IsIdempotent_SecondRunAddsNothing()
    {
        using var db = NewDb();
        SeedData.Ensure(db);
        var (schools, groups, students, devices, games, points, links) =
            (db.Schools.Count(), db.Groups.Count(), db.Students.Count(),
             db.Devices.Count(), db.Games.Count(), db.RatingPoints.Count(), db.ShareLinks.Count());

        SeedData.Ensure(db); // повторный запуск — гарды не дают дубликатов

        Assert.Equal(schools, db.Schools.Count());
        Assert.Equal(groups, db.Groups.Count());
        Assert.Equal(students, db.Students.Count());
        Assert.Equal(devices, db.Devices.Count());
        Assert.Equal(games, db.Games.Count());
        Assert.Equal(points, db.RatingPoints.Count());
        Assert.Equal(links, db.ShareLinks.Count());
    }
}
