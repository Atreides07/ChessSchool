using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Data;

/// <summary>Демо-данные для локального запуска: школа, группа, ученики с историей рейтинга и партиями.</summary>
public static class SeedData
{
    public static readonly Guid SchoolId = Demo.SchoolId;
    public static readonly Guid GroupId = Demo.GroupId;

    public static void Ensure(SchoolDbContext db)
    {
        if (db.Schools.Any()) return;

        var school = new School { Id = SchoolId, Name = "Шахматная школа №1" };
        var group = new Group { Id = GroupId, SchoolId = SchoolId, Name = "Группа начинающих" };
        db.Schools.Add(school);
        db.Groups.Add(group);

        var names = new[]
        {
            ("Иван Петров", 1340, "demo-user-ivan"),
            ("Мария Сидорова", 1520, "demo-user-maria"),
            ("Алексей Кузнецов", 1180, null as string),
            ("Дарья Орлова", 1410, null),
            ("Никита Волков", 1265, null)
        };

        var baseDate = DateTimeOffset.UtcNow.AddDays(-60);
        foreach (var (name, rating, sub) in names)
        {
            var student = new Student
            {
                GroupId = GroupId,
                DisplayName = name,
                Rating = rating,
                LinkedUserSub = sub,
                ConsentGranted = true,
                GamesPlayed = 12,
                Wins = 6,
                Draws = 3,
                Losses = 3
            };
            db.Students.Add(student);

            // История рейтинга — плавный рост к текущему значению.
            int start = rating - 120;
            for (int i = 0; i <= 12; i++)
            {
                db.RatingPoints.Add(new RatingPoint
                {
                    StudentId = student.Id,
                    Date = baseDate.AddDays(i * 5),
                    Rating = start + (rating - start) * i / 12
                });
            }
        }

        // Пара тренировочных партий без атрибуции — попадут в очередь тренера.
        db.Games.Add(new Game
        {
            Source = AttributionSource.None,
            Status = GameStatus.WaitingForOpponent,
            PlayedAt = DateTimeOffset.UtcNow.AddHours(-2),
            DeviceRef = "board-03",
            Pgn = "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 *",
            Result = GameResult.Ongoing
        });

        db.SaveChanges();
    }
}
