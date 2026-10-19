namespace PlayerApp;

internal static class AudioDevicePresentation
{
    // Windows EndpointFormFactor values; never infer device types from product names.
    public static string Glyph(int formFactor) => formFactor switch
    {
        0 => "\uE968", // Network
        2 or 7 or 8 => "\uE95F", // Wired/digital output
        3 => "\uE7F6", // Headphones
        4 => "\uE720", // Microphone
        5 => "\uE95B", // Headset
        6 => "\uE717", // Handset
        9 => "\uE7F4", // Display audio
        _ => "\uE767" // Speakers or unspecified output
    };
}
