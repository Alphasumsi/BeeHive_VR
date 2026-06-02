using System.Collections.Generic;

namespace BeeHiveVR.Models;

/// <summary>
/// Statische Beispiel-Daten für Entwicklung und Demo-Modus.
/// Im fertigen Projekt wird der Mock-Modus über einen Schalter
/// in den Einstellungen aktiviert (statt JSON zu laden).
/// </summary>
public static class MockData
{
    public static List<CarLayoutModel> CreateLayouts() => new()
    {
        new CarLayoutModel
        {
            CarName   = "Template",
            IsDefault = true,
            Sessions  = CreateDefaultSessions()
        },
        new CarLayoutModel
        {
            CarName    = "Porsche 992.2 Cup",
            CarClass   = "PCup",
            IsFavorite = true,
            Sessions   = CreatePorscheSessions()
        },
        new CarLayoutModel
        {
            CarName  = "Dallara IR18",
            CarClass = "IndyCar",
            Sessions = new() { new SessionConfigModel { Session = SessionType.Practice } }
        },
        new CarLayoutModel
        {
            CarName  = "Mazda MX-5",
            CarClass = "MX5",
            Sessions = new() { new SessionConfigModel { Session = SessionType.Practice } }
        },
        new CarLayoutModel
        {
            CarName  = "Mercedes AMG Evo GT3",
            CarClass = "GT3",
            Sessions = new() { new SessionConfigModel { Session = SessionType.Practice } }
        }
    };

    private static List<SessionConfigModel> CreateDefaultSessions() => new()
    {
        new SessionConfigModel
        {
            Session = SessionType.Practice,
            Sources = new()
            {
                new SourceModel
                {
                    Id = "fuel", Name = "Fuel calculator",
                    Type = SourceType.Browser, Target = "http://localhost:8888/fuel",
                    X = 0.0f, Y = -0.2f, Z = -0.8f, Opacity = 0.9f
                }
            }
        },
        new SessionConfigModel { Session = SessionType.Qualify },
        new SessionConfigModel { Session = SessionType.Race },
        new SessionConfigModel { Session = SessionType.TestDrive },
    };

    private static List<SessionConfigModel> CreatePorscheSessions() => new()
    {
        new SessionConfigModel
        {
            Session = SessionType.Practice,
            Sources = new()
            {
                new SourceModel
                {
                    Id = "fuel", Name = "Fuel calculator",
                    Type = SourceType.Browser, Target = "http://localhost:8888/fuel",
                    X = 0.0f, Y = -0.2f, Z = -0.8f, Yaw = 5f, Opacity = 0.9f
                },
                new SourceModel
                {
                    Id = "inputs", Name = "Inputs",
                    Type = SourceType.Window, Target = "Wheel Data",
                    X = 0.4f, Y = -0.3f, Z = -0.7f, Opacity = 1.0f
                }
            }
        },
        new SessionConfigModel { Session = SessionType.Qualify },
        new SessionConfigModel { Session = SessionType.Race },
        new SessionConfigModel { Session = SessionType.TestDrive },
    };
}