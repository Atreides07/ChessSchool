using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Domain;

public class School
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<Group> Groups { get; } = [];
}

public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SchoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Student> Students { get; } = [];
}

public class Student
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }

    /// <summary>Sub пользователя из IdP — связывает ученика с его онлайн-партиями.</summary>
    public string? LinkedUserSub { get; set; }

    // Текущий рейтинг и параметры Glicko-2 (отклонение RD и волатильность).
    public int Rating { get; set; } = 1200;
    public int RatingDeviation { get; set; } = 350;
    public double Volatility { get; set; } = 0.06;

    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }

    public bool ConsentGranted { get; set; }
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SchoolId { get; set; }
    public string Ref { get; set; } = string.Empty;
}

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AttributionSource Source { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Finished;
    public DateTimeOffset PlayedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Pgn { get; set; } = string.Empty;
    public string? DeviceRef { get; set; }

    public Guid? WhiteStudentId { get; set; }
    public Guid? BlackStudentId { get; set; }
    public GameResult Result { get; set; }
    public GameEndReason EndReason { get; set; }

    public int WhiteRatingChange { get; set; }
    public int BlackRatingChange { get; set; }

    /// <summary>Идемпотентность приёма онлайн-партий.</summary>
    public string? ExternalGameId { get; set; }
}

public class RatingPoint
{
    public long Id { get; set; }
    public Guid StudentId { get; set; }
    public DateTimeOffset Date { get; set; }
    public int Rating { get; set; }
}

public class ShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}
