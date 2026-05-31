namespace HoneyOverlays.Services;

/// <summary>
/// Wo befindet sich der Spieler aktuell? Abgeleitet aus iRacing's
/// PlayerTrackSurface + IsInGarage. Nutzbar für Overlay-Filter
/// ("zeige Pit-Helper nur in der Pit", etc.).
/// </summary>
public enum TrackLocation
{
    Unknown,
    NotInWorld,    // nicht im Auto / Replay / Spectator
    OutOfCar,      // Fahrer ausgestiegen (Auto steht irgendwo)
    InGarage,      // in der Garage geparkt
    InPit,         // auf der Boxenstraße im Auto
    OnTrack,       // aktiv auf der Strecke
    OffTrack       // im Kies / neben der Strecke
}