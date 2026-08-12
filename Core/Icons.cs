namespace ClaudeScord;

// Discord's own icon geometry, lifted from the live client.
//
// How these were obtained, so they can be refreshed after a redesign: Discord's chrome icons are
// inline <svg> in the DOM, so each one's <path d> is read straight off the page. The composer and
// tray icons (sticker, gift, headset, gear) are *Lottie* animations rather than static SVGs — their
// paths live in a 1000-unit animation space with the placement carried on parent <g transform>
// matrices, so each path's getCTM() has to be baked into its own data before it means anything.
// Lottie only ever emits M/L/C/z with absolute coordinates, which makes that bake a straight
// "map every coordinate pair through the matrix". Those authored at 20 units were then scaled by
// 1.2 so that every constant in this file shares one 24-unit viewBox.
//
// Everything below is FILLED geometry (Svg.SvgFill) unless it is listed in Stroked. Discord has no
// stroked icons; the few still in that set are hand-authored stand-ins for ones not yet lifted, and
// each should be replaced with real geometry rather than kept.
static class Icons
{
    // The guild-icon mask. Authored in a 40-unit space (the live <svg> is viewBox="-4 -4 48 48",
    // where the extra 4 either side is bleed for the notification-badge cutout, not the icon).
    //
    // This is a *squircle*, not a rounded rectangle — continuous curvature, the same construction
    // iOS uses. The giveaway is the control points: a rounded rect would place them on the arc, this
    // puts them at 1.18902/5.95647. Drawing it as a rounded rect is close enough to look right in a
    // screenshot and wrong enough to notice side by side.
    public const float GuildViewBox = 40f;
    public const string GuildSquircle =
        "M0 17.4545C0 11.3449 0 8.29005 1.18902 5.95647C2.23491 3.90379 3.90379 2.23491 5.95647 1.18902" +
        "C8.29005 0 11.3449 0 17.4545 0H22.5455C28.6551 0 31.71 0 34.0435 1.18902C36.0962 2.23491 37.7651 3.90379 38.811 5.95647" +
        "C40 8.29005 40 11.3449 40 17.4545V22.5455C40 28.6551 40 31.71 38.811 34.0435C37.7651 36.0962 36.0962 37.7651 34.0435 38.811" +
        "C31.71 40 28.6551 40 22.5455 40H17.4545C11.3449 40 8.29005 40 5.95647 38.811C3.90379 37.7651 2.23491 36.0962 1.18902 34.0435" +
        "C0 31.71 0 28.6551 0 22.5455V17.4545Z";

    // The Clyde mark, 24-unit. Used for the Home / DMs button at the top of the rail.
    //
    // Mind the line joins. A path split across C# string concatenation loses nothing *visible*, but
    // if one line ends in a digit and the next begins with one the two numbers silently weld into a
    // third: this path used to end line 4 with "-2.27" and start line 5 with "0-1.24", which the
    // parser read as "-2.270" followed by "-1.24" — one coordinate short, so Clyde's left eye came
    // out as a crescent while the right one, whose join happened to fall elsewhere, was perfect.
    // Break lines after a command letter, or leave a leading space, as below.
    public const string Clyde =
        "M19.73 4.87a18.2 18.2 0 0 0-4.6-1.44c-.21.4-.4.8-.58 1.21-1.69-.25-3.4-.25-5.1 0-.18-.41-.37-.82-.59-1.2" +
        "-1.6.27-3.14.75-4.6 1.43A19.04 19.04 0 0 0 .96 17.7a18.43 18.43 0 0 0 5.63 2.87c.46-.62.86-1.28 1.2-1.98" +
        "-.65-.25-1.29-.55-1.9-.92.17-.12.32-.24.47-.37 3.58 1.7 7.7 1.7 11.28 0l.46.37c-.6.36-1.25.67-1.9.92" +
        ".35.7.75 1.35 1.2 1.98 2.03-.63 3.94-1.6 5.64-2.87.47-4.87-.78-9.09-3.3-12.83ZM8.3 15.12c-1.1 0-2-1.02-2-2.27" +
        " 0-1.24.88-2.26 2-2.26s2.02 1.02 2 2.26c0 1.25-.89 2.27-2 2.27Zm7.4 0c-1.1 0-2-1.02-2-2.27 0-1.24.88-2.26 2-2.26" +
        "s2.02 1.02 2 2.26c0 1.25-.88 2.27-2 2.27Z";

    // ── Channel list ────────────────────────────────────────────────────────────────────────────
    // Discord's hash is a slanted, rounded "#" — the typographic character from any UI font is
    // visibly wrong next to it.
    public const string Hash =
        "M10.99 3.16A1 1 0 1 0 9 2.84L8.15 8H4a1 1 0 0 0 0 2h3.82l-.67 4H3a1 1 0 1 0 0 2h3.82l-.8 4.84a1 1 0 0 0 1.97.32" +
        "L8.85 16h4.97l-.8 4.84a1 1 0 0 0 1.97.32l.86-5.16H20a1 1 0 1 0 0-2h-3.82l.67-4H21a1 1 0 1 0 0-2h-3.82l.8-4.84" +
        "a1 1 0 1 0-1.97-.32L15.15 8h-4.97l.8-4.84ZM14.15 14l.67-4H9.85l-.67 4h4.97Z";

    // Voice channel: a filled speaker plus the two arcs, as two subpaths.
    public const string Speaker =
        "M12 3a1 1 0 0 0-1-1h-.06a1 1 0 0 0-.74.32L5.92 7H3a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h2.92l4.28 4.68a1 1 0 0 0 .74.32H11a1 1 0 0 0 1-1V3Z" +
        "M15.1 20.75c-.58.14-1.1-.33-1.1-.92v-.03c0-.5.37-.92.85-1.05a7 7 0 0 0 0-13.5A1.11 1.11 0 0 1 14 4.2v-.03c0-.6.52-1.06 1.1-.92a9 9 0 0 1 0 17.5Z" +
        "M15.16 16.51c-.57.28-1.16-.2-1.16-.83v-.14c0-.43.28-.8.63-1.02a3 3 0 0 0 0-5.04c-.35-.23-.63-.6-.63-1.02v-.14c0-.63.59-1.1 1.16-.83a5 5 0 0 1 0 9.02Z";

    // Threads. Not three stacked lines — Discord's is a pair of nested chevron-like sheets.
    public const string ThreadLine =
        "M12 2.81a1 1 0 0 1 0-1.41l.36-.36a1 1 0 0 1 1.41 0l9.2 9.2a1 1 0 0 1 0 1.4l-.7.7a1 1 0 0 1-1.3.13l-9.54-6.72a1 1 0 0 1-.08-1.58l1-1L12 2.8Z" +
        "M12 21.2a1 1 0 0 1 0 1.41l-.35.35a1 1 0 0 1-1.41 0l-9.2-9.19a1 1 0 0 1 0-1.41l.7-.7a1 1 0 0 1 1.3-.12l9.54 6.72a1 1 0 0 1 .07 1.58l-1 1 .35.36Z" +
        "M15.66 16.8a1 1 0 0 1-1.38.28l-8.49-5.66A1 1 0 1 1 6.9 9.76l8.49 5.65a1 1 0 0 1 .27 1.39Z" +
        "M17.1 14.25a1 1 0 1 0 1.11-1.66L9.73 6.93a1 1 0 0 0-1.11 1.66l8.49 5.66Z";

    // Forum channel: the big speech bubble with a smaller one overlapping its lower right. The
    // sidebar used to borrow ThreadLine for these, which is a different glyph in the live client.
    public const string ForumLine =
        "M18.91 12.98a5.45 5.45 0 0 1 2.18 6.2c-.1.33-.09.68.1.96l.83 1.32a1 1 0 0 1-.84 1.54h-5.5" +
        "A5.6 5.6 0 0 1 10 17.5a5.6 5.6 0 0 1 5.68-5.5c1.2 0 2.32.36 3.23.98Z" +
        "M19.24 10.86c.32.16.72-.02.74-.38L20 10c0-4.42-4.03-8-9-8s-9 3.58-9 8c0 1.5.47 2.91 1.28 4.11" +
        ".14.21.12.49-.06.67l-1.51 1.51A1 1 0 0 0 2.4 18H11c.05 0 .1-.04.1-.1v-.4c0-3.66 3.03-6.62 6.7-6.62" +
        ".5 0 1 .05 1.44.15Z";

    // ── Chrome ──────────────────────────────────────────────────────────────────────────────────
    public const string SearchLine =
        "M15.62 17.03a9 9 0 1 1 1.41-1.41l4.68 4.67a1 1 0 0 1-1.42 1.42l-4.67-4.68ZM17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0Z";
    public const string PlusLine =
        "M13 3a1 1 0 1 0-2 0v8H3a1 1 0 1 0 0 2h8v8a1 1 0 0 0 2 0v-8h8a1 1 0 0 0 0-2h-8V3Z";
    public const string CloseLine =
        "M17.3 18.7a1 1 0 0 0 1.4-1.4L13.42 12l5.3-5.3a1 1 0 0 0-1.42-1.4L12 10.58l-5.3-5.3a1 1 0 0 0-1.4 1.42L10.58 12l-5.3 5.3a1 1 0 1 0 1.42 1.4L12 13.42l5.3 5.3Z";
    public const string ChevronDown =
        "M5.3 9.3a1 1 0 0 1 1.4 0l5.3 5.29 5.3-5.3a1 1 0 1 1 1.4 1.42l-6 6a1 1 0 0 1-1.4 0l-6-6a1 1 0 0 1 0-1.42Z";
    public const string ChevronRight =
        "M9.3 5.3a1 1 0 0 0 0 1.4l5.29 5.3-5.3 5.3a1 1 0 1 0 1.42 1.4l6-6a1 1 0 0 0 0-1.4l-6-6a1 1 0 0 0-1.42 0Z";
    public const string PinLine =
        "M19.38 11.38a3 3 0 0 0 4.24 0l.03-.03a.5.5 0 0 0 0-.7L13.35.35a.5.5 0 0 0-.7 0l-.03.03a3 3 0 0 0 0 4.24L13 5l-2.92 2.92-3.65-.34a2 2 0 0 0-1.6.58l-.62.63a1 1 0 0 0 0 1.42l9.58 9.58a1 1 0 0 0 1.42 0l.63-.63a2 2 0 0 0 .58-1.6l-.34-3.64L19 11l.38.38Z" +
        "M9.07 17.07a.5.5 0 0 1-.08.77l-5.15 3.43a.5.5 0 0 1-.63-.06l-.42-.42a.5.5 0 0 1-.06-.63L6.16 15a.5.5 0 0 1 .77-.08l2.14 2.14Z";
    public const string InboxLine =
        "M5 2a3 3 0 0 0-3 3v14a3 3 0 0 0 3 3h14a3 3 0 0 0 3-3V5a3 3 0 0 0-3-3H5ZM4 5.5C4 4.67 4.67 4 5.5 4h13c.83 0 1.5.67 1.5 1.5v6c0 .83-.67 1.5-1.5 1.5h-2.65c-.5 0-.85.5-.85 1a3 3 0 1 1-6 0c0-.5-.35-1-.85-1H5.5A1.5 1.5 0 0 1 4 11.5v-6Z";
    public const string HelpLine =
        "M12 23a11 11 0 1 0 0-22 11 11 0 0 0 0 22Zm-.28-16c-.98 0-1.81.47-2.27 1.14A1 1 0 1 1 7.8 7.01 4.73 4.73 0 0 1 11.72 5c2.5 0 4.65 1.88 4.65 4.38 0 2.1-1.54 3.77-3.52 4.24l.14 1a1 1 0 0 1-1.98.27l-.28-2a1 1 0 0 1 .99-1.14c1.54 0 2.65-1.14 2.65-2.38 0-1.23-1.1-2.37-2.65-2.37ZM13 17.88a1.13 1.13 0 1 1-2.25 0 1.13 1.13 0 0 1 2.25 0Z";

    // Members toggle / group DM.
    public const string People =
        "M14.5 8a3 3 0 1 0-2.7-4.3c-.2.4.06.86.44 1.12a5 5 0 0 1 2.14 3.08c.01.06.06.1.12.1Z" +
        "M18.44 17.27c.15.43.54.73 1 .73h1.06c.83 0 1.5-.67 1.5-1.5a7.5 7.5 0 0 0-6.5-7.43c-.55-.08-.99.38-1.1.92-.06.3-.15.6-.26.87-.23.58-.05 1.3.47 1.63a9.53 9.53 0 0 1 3.83 4.78Z" +
        "M12.5 9a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM2 20.5a7.5 7.5 0 0 1 15 0c0 .83-.67 1.5-1.5 1.5a.2.2 0 0 1-.2-.16c-.2-.96-.56-1.87-.88-2.54-.1-.23-.42-.15-.42.1v2.1a.5.5 0 0 1-.5.5h-8a.5.5 0 0 1-.5-.5v-2.1c0-.25-.31-.33-.42-.1-.32.67-.67 1.58-.88 2.54a.2.2 0 0 1-.2.16A1.5 1.5 0 0 1 2 20.5Z";

    // "Invite to Server", top-right of the guild header. Two paths: the pair of figures, then the
    // plus badge over them.
    public const string PersonAdd =
        "M14.5 8a3 3 0 1 0-2.7-4.3c-.2.4.06.86.44 1.12a5 5 0 0 1 2.14 3.08c.01.06.06.1.12.1ZM16.62 13.17c-.22.29-.65.37-.92.14-.34-.3-.7-.57-1.09-.82-.52-.33-.7-1.05-.47-1.63.11-.27.2-.57.26-.87.11-.54.55-1 1.1-.92 1.6.2 3.04.92 4.15 1.98.3.27-.25.95-.65.95a3 3 0 0 0-2.38 1.17ZM15.19 15.61c.13.16.02.39-.19.39a3 3 0 0 0-1.52 5.59c.2.12.26.41.02.41h-8a.5.5 0 0 1-.5-.5v-2.1c0-.25-.31-.33-.42-.1-.32.67-.67 1.58-.88 2.54a.2.2 0 0 1-.2.16A1.5 1.5 0 0 1 2 20.5a7.5 7.5 0 0 1 13.19-4.89ZM9.5 12a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM15.5 22Z" +
        "M19 14a1 1 0 0 1 1 1v3h3a1 1 0 0 1 0 2h-3v3a1 1 0 0 1-2 0v-3h-3a1 1 0 1 1 0-2h3v-3a1 1 0 0 1 1-1Z";

    // The figure with a tick — the left of the two round buttons over a profile panel's banner,
    // which reads "Friend" when you already are one.
    public const string PersonCheck =
        "M12 10a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM11.53 11A9.53 9.53 0 0 0 2 20.53c0 .81.66 1.47 1.47 1.47h.22c.24 0 .44-.17.5-.4.29-1.12.84-2.17 1.32-2.91.14-.21.43-.1.4.15l-.26 2.61c-.02.3.2.55.5.55h6.4a.5.5 0 0 0 .35-.85l-.02-.03a3 3 0 1 1 4.24-4.24l.53.52c.2.2.5.2.7 0l1.8-1.8c.17-.17.2-.43.06-.62A9.52 9.52 0 0 0 12.47 11h-.94Z" +
        "M23.7 17.7a1 1 0 1 0-1.4-1.4L18 20.58l-2.3-2.3a1 1 0 0 0-1.4 1.42l3 3a1 1 0 0 0 1.4 0l5-5Z";

    // The composer's "Apps" button — the four-shape sparkle cluster. A Lottie icon, so each path's
    // getCTM() was baked into its own coordinates and the 96-unit animation space folded to 24 (see
    // the note at the top of this file).
    public const string AppsLine =
        "M2.137 7.654C1.884 8.599 2.444 9.571 3.39 9.825C3.39 9.825 7.669 10.971 7.669 10.971C8.614 11.225 9.587 10.663 9.84 9.718" +
        "C9.84 9.718 10.986 5.438 10.986 5.438C11.239 4.493 10.678 3.521 9.733 3.268C9.733 3.268 5.454 2.121 5.454 2.121" +
        "C4.509 1.868 3.536 2.429 3.283 3.374C3.283 3.374 2.137 7.654 2.137 7.654Z" +
        "M13.018 7.908C12.293 9.236 13.255 10.856 14.768 10.856C14.768 10.856 20.013 10.856 20.013 10.856" +
        "C21.527 10.856 22.488 9.236 21.764 7.908C21.764 7.908 19.141 3.1 19.141 3.1C18.385 1.714 16.396 1.714 15.64 3.1" +
        "C15.64 3.1 13.018 7.908 13.018 7.908Z" +
        "M5.926 13.293C6.277 12.936 6.845 12.936 7.197 13.293C7.197 13.293 7.95 14.06 7.95 14.06C8.094 14.205 8.281 14.298 8.482 14.321" +
        "C8.482 14.321 9.539 14.445 9.539 14.445C10.032 14.502 10.386 14.955 10.331 15.458C10.331 15.458 10.214 16.537 10.214 16.537" +
        "C10.191 16.743 10.238 16.95 10.345 17.125C10.345 17.125 10.91 18.045 10.91 18.045C11.173 18.474 11.047 19.039 10.627 19.309" +
        "C10.627 19.309 9.728 19.888 9.728 19.888C9.556 19.998 9.426 20.165 9.359 20.36C9.359 20.36 9.006 21.384 9.006 21.384" +
        "C8.841 21.861 8.33 22.112 7.861 21.946C7.861 21.946 6.857 21.589 6.857 21.589C6.665 21.522 6.457 21.522 6.266 21.589" +
        "C6.266 21.589 5.261 21.946 5.261 21.946C4.793 22.112 4.281 21.861 4.116 21.384C4.116 21.384 3.763 20.36 3.763 20.36" +
        "C3.697 20.165 3.566 19.998 3.395 19.888C3.395 19.888 2.495 19.309 2.495 19.309C2.076 19.039 1.95 18.474 2.212 18.045" +
        "C2.212 18.045 2.777 17.125 2.777 17.125C2.885 16.95 2.931 16.743 2.908 16.537C2.908 16.537 2.791 15.458 2.791 15.458" +
        "C2.737 14.955 3.09 14.502 3.583 14.445C3.583 14.445 4.64 14.321 4.64 14.321C4.841 14.298 5.029 14.205 5.173 14.06" +
        "C5.173 14.06 5.926 13.293 5.926 13.293Z" +
        "M16.505 21.286C16.856 22.234 18.198 22.234 18.549 21.286C18.549 21.286 19.006 20.05 19.006 20.05" +
        "C19.187 19.559 19.575 19.172 20.066 18.99C20.066 18.99 21.301 18.533 21.301 18.533C22.25 18.182 22.25 16.841 21.301 16.49" +
        "C21.301 16.49 20.066 16.033 20.066 16.033C19.575 15.851 19.187 15.464 19.006 14.973C19.006 14.973 18.549 13.737 18.549 13.737" +
        "C18.198 12.789 16.856 12.789 16.505 13.737C16.505 13.737 16.048 14.973 16.048 14.973C15.866 15.464 15.479 15.851 14.988 16.033" +
        "C14.988 16.033 13.752 16.49 13.752 16.49C12.804 16.841 12.804 18.182 13.752 18.533C13.752 18.533 14.988 18.99 14.988 18.99" +
        "C15.479 19.172 15.866 19.559 16.048 20.05C16.048 20.05 16.505 21.286 16.505 21.286Z";

    // The compass in the rail's "Discover" tile.
    public const string Compass =
        "M12 23a11 11 0 1 0 0-22 11 11 0 0 0 0 22Zm4.7-15.7a1 1 0 0 1 .25 1L15.2 13.6a3 3 0 0 1-1.9 1.9l-4.3 1.43" +
        "a1 1 0 0 1-1.26-1.27l1.43-4.3a3 3 0 0 1 1.9-1.89l4.32-1.44a1 1 0 0 1 1 .25ZM12 13.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z";

    // The bell with a slash — Discord's "Notification Settings" button carries the muted state.
    public const string BellLine =
        "M1.3 21.3a1 1 0 1 0 1.4 1.4l20-20a1 1 0 0 0-1.4-1.4l-20 20ZM3.13 16.13c.11.27.46.28.66.08L15.73 4.27a.47.47 0 0 0-.07-.74 6.97 6.97 0 0 0-1.35-.64.62.62 0 0 1-.38-.43 2 2 0 0 0-3.86 0 .62.62 0 0 1-.38.43A7 7 0 0 0 5 9.5v2.09a.5.5 0 0 1-.13.33l-1.1 1.22A3 3 0 0 0 3 15.15v.28c0 .24.04.48.13.7Z" +
        "M18.64 9.36c.13-.13.36-.05.36.14v2.09c0 .12.05.24.13.33l1.1 1.22a3 3 0 0 1 .77 2.01v.28c0 .67-.34 1.29-.95 1.56-1.31.6-4 1.51-8.05 1.51-.46 0-.9-.01-1.33-.03a.48.48 0 0 1-.3-.83l8.27-8.28Z" +
        "M9.18 19.84A.16.16 0 0 0 9 20a3 3 0 1 0 6 0c0-.1-.09-.17-.18-.16a24.84 24.84 0 0 1-5.64 0Z";

    // ── Message actions ─────────────────────────────────────────────────────────────────────────
    public const string SmileyLine =
        "M12 23a11 11 0 1 0 0-22 11 11 0 0 0 0 22ZM6.5 13a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Zm11 0a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Zm-9.8 1.17a1 1 0 0 1 1.39.27 3.5 3.5 0 0 0 5.82 0 1 1 0 0 1 1.66 1.12 5.5 5.5 0 0 1-9.14 0 1 1 0 0 1 .27-1.4Z";
    public const string ReplyLine =
        "M2.3 7.3a1 1 0 0 0 0 1.4l5 5a1 1 0 0 0 1.4-1.4L5.42 9H11a7 7 0 0 1 7 7v4a1 1 0 1 0 2 0v-4a9 9 0 0 0-9-9H5.41l3.3-3.3a1 1 0 0 0-1.42-1.4l-5 5Z";
    public const string ForwardLine =
        "M21.7 7.3a1 1 0 0 1 0 1.4l-5 5a1 1 0 0 1-1.4-1.4L18.58 9H13a7 7 0 0 0-7 7v4a1 1 0 1 1-2 0v-4a9 9 0 0 1 9-9h5.59l-3.3-3.3a1 1 0 0 1 1.42-1.4l5 5Z";
    public const string DotsHorizontal =
        "M4 14a2 2 0 1 0 0-4 2 2 0 0 0 0 4Zm10-2a2 2 0 1 1-4 0 2 2 0 0 1 4 0Zm8 0a2 2 0 1 1-4 0 2 2 0 0 1 4 0Z";
    // The checkmark inside the verified-app tag, and every other confirmation tick.
    public const string CheckLine =
        "M19.06 6.94a1.5 1.5 0 0 1 0 2.12l-8 8a1.5 1.5 0 0 1-2.12 0l-4-4a1.5 1.5 0 0 1 2.12-2.12L10 13.88l6.94-6.94a1.5 1.5 0 0 1 2.12 0Z";
    public const string DownloadLine =
        "M12 2a1 1 0 0 1 1 1v10.59l3.3-3.3a1 1 0 1 1 1.4 1.42l-5 5a1 1 0 0 1-1.4 0l-5-5a1 1 0 1 1 1.4-1.42l3.3 3.3V3a1 1 0 0 1 1-1ZM3 20a1 1 0 1 0 0 2h18a1 1 0 1 0 0-2H3Z";
    // Jump-to-present: the plain arrow, without the download rule under it.
    public const string ArrowDownLine =
        "M12 2a1 1 0 0 1 1 1v15.59l5.3-5.3a1 1 0 0 1 1.4 1.42l-7 7a1 1 0 0 1-1.4 0l-7-7a1 1 0 1 1 1.4-1.42l5.3 5.3V3a1 1 0 0 1 1-1Z";
    public const string Crown =
        "M5 18a1 1 0 0 0-1 1 3 3 0 0 0 3 3h10a3 3 0 0 0 3-3 1 1 0 0 0-1-1H5ZM3.04 7.76a1 1 0 0 0-1.52 1.15l2.25 6.42a1 1 0 0 0 .94.67h14.55a1 1 0 0 0 .95-.71l1.94-6.45a1 1 0 0 0-1.55-1.1l-4.11 3-3.55-5.33.82-.82a.83.83 0 0 0 0-1.18l-1.17-1.17a.83.83 0 0 0-1.18 0l-1.17 1.17a.83.83 0 0 0 0 1.18l.82.82-3.61 5.42-4.41-3.07Z";

    // ── Composer + tray (baked out of Discord's Lottie sources; see the note at the top) ────────
    public const string StickerLine =
        "M6 2 C6 2 18 2 18 2 C20.21 2 22 3.79 22 6 C22 6 22 13.5 22 13.5 C22 13.78 21.78 14 21.5 14 C21.5 14 19 14 19 14 C16.24 14 14 16.24 14 19 C14 19 14 21.5 14 21.5 C14 21.78 13.78 22 13.5 22 C13.5 22 6 22 6 22 C3.79 22 2 20.21 2 18 C2 18 2 6 2 6 C2 3.79 3.79 2 6 2 Z" +
        "M21.66 16 C21.69 16 21.71 16.03 21.7 16.06 C21.56 16.36 21.36 16.64 21.12 16.88 C21.12 16.88 16.88 21.12 16.88 21.12 C16.64 21.36 16.36 21.56 16.06 21.7 C16.03 21.71 16 21.69 16 21.66 C16 21.66 16 21.17 16 21.17 C16 21.17 16 19 16 19 C16 17.34 17.34 16 19 16 C19 16 21.17 16 21.17 16 C21.17 16 21.66 16 21.66 16 Z" +
        "M6.5 10 C7.33 10 8 9.33 8 8.5 C8 7.67 7.33 7 6.5 7 C5.67 7 5 7.67 5 8.5 C5 9.33 5.67 10 6.5 10 Z" +
        "M19 8.5 C19 7.67 18.33 7 17.5 7 C16.67 7 16 7.67 16 8.5 C16 9.33 16.67 10 17.5 10 C18.33 10 19 9.33 19 8.5 Z" +
        "M9.09 11.44 C8.78 10.98 8.16 10.86 7.7 11.17 C7.24 11.48 7.12 12.1 7.43 12.56 C8.41 14.03 10.09 15 12 15 C13.91 15 15.59 14.03 16.57 12.56 C16.88 12.1 16.76 11.48 16.3 11.17 C15.84 10.86 15.22 10.98 14.91 11.44 C14.28 12.39 13.21 13 12 13 C10.79 13 9.72 12.39 9.09 11.44 Z";

    public const string GiftLine =
        "M5 22 C3.9 22 3 21.1 3 20 C3 20 3 14.5 3 14.5 C3 14.22 3.22 14 3.5 14 C3.5 14 10.5 14 10.5 14 C10.78 14 11 14.22 11 14.5 C11 14.5 11 21.5 11 21.5 C11 21.78 10.78 22 10.5 22 C10.5 22 5 22 5 22 Z" +
        "M13 21.5 C13 21.78 13.22 22 13.5 22 C13.5 22 19 22 19 22 C20.1 22 21 21.1 21 20 C21 20 21 14.5 21 14.5 C21 14.22 20.78 14 20.5 14 C20.5 14 13.5 14 13.5 14 C13.22 14 13 14.22 13 14.5 C13 14.5 13 21.5 13 21.5 Z" +
        "M2 10 C2 8.9 2.9 8 4 8 C4 8 20 8 20 8 C21.1 8 22 8.9 22 10 C22 10 22 11.5 22 11.5 C22 11.78 21.78 12 21.5 12 C21.5 12 2.5 12 2.5 12 C2.22 12 2 11.78 2 11.5 C2 11.5 2 10 2 10 Z" +
        "M19 6 C19 4.34 17.66 3 16 3 C16 3 15.91 3 15.91 3 C14.49 3 13.26 3.96 12.92 5.34 C12.92 5.34 12 9 12 9 C12 9 16 9 16 9 C17.66 9 19 7.66 19 6 C19 6 19 6 19 6 Z" +
        "M5 6 C5 4.34 6.34 3 8 3 C8 3 8.09 3 8.09 3 C9.51 3 10.74 3.96 11.09 5.34 C11.09 5.34 12 9 12 9 C12 9 8 9 8 9 C6.34 9 5 7.66 5 6 C5 6 5 6 5 6 Z";

    public const string HeadsetLine =
        "M3.68 12 C3.68 7.41 7.41 3.68 12 3.68 C16.59 3.68 20.32 7.41 20.32 12 C20.32 12.72 20.27 13.41 20.17 14.08 C20.17 14.08 18.24 14.08 18.24 14.08 C17.26 14.08 16.33 14.54 15.74 15.33 C15.74 15.33 13.69 18.06 13.69 18.06 C13.2 18.71 13.08 19.57 13.36 20.33 C13.96 22 16.04 23 17.7 21.8 C21.19 19.27 22.4 15.81 22.4 12 C22.4 6.26 17.74 1.6 12 1.6 C6.26 1.6 1.6 6.26 1.6 12 C1.6 15.81 2.81 19.27 6.3 21.8 C7.96 23 10.04 22 10.64 20.33 C10.92 19.57 10.8 18.71 10.31 18.06 C10.31 18.06 8.26 15.33 8.26 15.33 C7.67 14.54 6.74 14.08 5.76 14.08 C5.76 14.08 3.83 14.08 3.83 14.08 C3.73 13.41 3.68 12.72 3.68 12 Z";

    public const string GearLine =
        "M10.56 1.09 C10.11 1.15 9.85 1.62 9.92 2.08 C10.1 3.24 9.74 4.28 8.94 4.61 C8.14 4.94 7.15 4.47 6.45 3.51 C6.18 3.15 5.67 2.99 5.31 3.27 C4.54 3.86 3.86 4.54 3.27 5.31 C2.99 5.67 3.15 6.18 3.51 6.45 C4.47 7.15 4.94 8.14 4.61 8.94 C4.28 9.74 3.24 10.1 2.08 9.92 C1.62 9.85 1.15 10.11 1.09 10.56 C1.03 11.03 1 11.51 1 12 C1 12.49 1.03 12.97 1.09 13.44 C1.15 13.89 1.62 14.15 2.08 14.08 C3.24 13.9 4.28 14.27 4.61 15.06 C4.94 15.86 4.47 16.85 3.51 17.55 C3.15 17.82 2.99 18.33 3.27 18.69 C3.86 19.46 4.54 20.14 5.31 20.73 C5.67 21.01 6.18 20.85 6.45 20.49 C7.15 19.53 8.14 19.06 8.94 19.39 C9.74 19.72 10.1 20.76 9.92 21.92 C9.85 22.38 10.11 22.85 10.56 22.91 C11.03 22.97 11.51 23 12 23 C12.49 23 12.97 22.97 13.44 22.91 C13.89 22.85 14.15 22.38 14.08 21.92 C13.9 20.76 14.27 19.72 15.06 19.39 C15.86 19.06 16.85 19.53 17.55 20.49 C17.82 20.85 18.33 21.01 18.69 20.73 C19.46 20.14 20.14 19.46 20.73 18.69 C21.01 18.33 20.85 17.82 20.49 17.55 C19.53 16.85 19.06 15.86 19.39 15.06 C19.72 14.27 20.76 13.9 21.92 14.08 C22.38 14.15 22.85 13.89 22.91 13.44 C22.97 12.97 23 12.49 23 12 C23 11.51 22.97 11.03 22.91 10.56 C22.85 10.11 22.38 9.85 21.92 9.92 C20.76 10.1 19.72 9.74 19.39 8.94 C19.06 8.14 19.53 7.15 20.49 6.45 C20.85 6.18 21.01 5.67 20.73 5.31 C20.14 4.54 19.46 3.86 18.69 3.27 C18.33 2.99 17.82 3.15 17.55 3.51 C16.85 4.47 15.86 4.94 15.06 4.61 C14.27 4.28 13.9 3.24 14.08 2.08 C14.15 1.62 13.89 1.15 13.44 1.09 C12.97 1.03 12.49 1 12 1 C11.51 1 11.03 1.03 10.56 1.09 Z" +
        "M16 12 C16 14.21 14.21 16 12 16 C9.79 16 8 14.21 8 12 C8 9.79 9.79 8 12 8 C14.21 8 16 9.79 16 12 Z";

    // Muted variants: Discord draws the same glyph with its slash on top, so append the rule the
    // "Notification Settings" bell already uses rather than authoring a second shape.
    const string Slash = "M1.3 21.3a1 1 0 1 0 1.4 1.4l20-20a1 1 0 0 0-1.4-1.4l-20 20Z";
    public const string HeadsetMutedLine = HeadsetLine + Slash;

    // ── mic ─────────────────────────────────────────────────────────────────────────────────────
    // The one compound icon. Discord's mic is three filled shapes (capsule, stem, foot) plus the
    // pickup arc, which is *stroked* — an open path with no inside, so filling it would produce a
    // solid crescent. Kept in its authored 20-unit space and drawn by Icons.Draw, which is the only
    // place that knows the mic needs two passes.
    const float MicViewBox = 20f;
    const string MicBody =
        "M13.33 5.03 C13.33 3.19 11.84 1.7 10 1.7 C8.16 1.7 6.67 3.19 6.67 5.03 C6.67 5.03 6.67 8.27 6.67 8.27 C6.67 10.12 8.16 11.6 10 11.6 C11.84 11.6 13.33 10.12 13.33 8.27 C13.33 8.27 13.33 5.03 13.33 5.03 Z" +
        "M10.83 14.1 C10.83 13.87 10.65 13.68 10.42 13.68 C10.42 13.68 9.58 13.68 9.58 13.68 C9.35 13.68 9.17 13.87 9.17 14.1 C9.17 14.1 9.17 17.43 9.17 17.43 C9.17 17.66 9.35 17.85 9.58 17.85 C9.58 17.85 10.42 17.85 10.42 17.85 C10.65 17.85 10.83 17.66 10.83 17.43 C10.83 17.43 10.83 14.1 10.83 14.1 Z" +
        "M7.5 16.6 C7.04 16.6 6.67 16.97 6.67 17.43 C6.67 17.89 7.04 18.27 7.5 18.27 C7.5 18.27 12.5 18.27 12.5 18.27 C12.96 18.27 13.33 17.89 13.33 17.43 C13.33 16.97 12.96 16.6 12.5 16.6 C12.5 16.6 7.5 16.6 7.5 16.6 Z";
    const string MicArc =
        "M15.83 8.28 C15.83 11.5 13.22 14.11 10 14.11 C6.78 14.11 4.17 11.5 4.17 8.28";

    /// Identity only — Draw recognises these and paints the real geometry above.
    public const string MicLine = "mic";
    public const string MicMutedLine = "mic-muted";

    // ── Still hand-authored: stroked centre-lines, listed in Stroked below ──────────────────────
    // Each of these is a stand-in for geometry not yet lifted off the live client. Replacing one
    // means moving its constant up into the filled section and deleting it from Stroked.
    public const string GifBox = "M4 5h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1Z";
    public const string PencilLine = "M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4L16.5 3.5Z";
    public const string FileLine = "M6 3h8l4 4v14a0 0 0 0 1 0 0H6a0 0 0 0 1 0 0V3ZM14 3v4h4";
    public const string Megaphone =
        "M19 4a1 1 0 0 0-1.6-.8L11 8H6a3 3 0 0 0-3 3v2a3 3 0 0 0 3 3l1 4.3a1 1 0 0 0 1 .7h1.6a1 1 0 0 0 1-1.3L9.6 16h1.4l6.4 4.8" +
        "A1 1 0 0 0 19 20V4Z";

    // ── Filled, from the live client ────────────────────────────────────────────────────────────
    public const string PlayTriangle = "M8 5l12 7-12 7V5Z";
    /// The green handset the live client puts beside a "started a call" system message. Lifted
    /// from its own asset, which is an 18x18 viewBox — pass 18f to SvgFill, not the default 24.
    public const string PhoneCall =
        "M17.7163041 15.36645368c-.0190957.02699568-1.9039523 2.6680735-2.9957762 2.63320406-3.0676659-.09785935-6.6733809-3.07188394-9.15694343-5.548738C3.08002193 9.9740657.09772497 6.3791404 0 3.3061316v-.024746C0 2.2060575 2.61386252.3152347 2.64082114.2972376c.7110335-.4971705 1.4917101-.3149497 1.80959713.1372281.19320342.2744561 2.19712724 3.2811005 2.42290565 3.6489167.09884826.1608492.14714912.3554431.14714912.5702838 0 .2744561-.07975258.5770327-.23701117.8751101-.1527655.2902036-.65262318 1.1664385-.89862055 1.594995.2673396.3768148.94804468 1.26429792 2.351016 2.66357424 1.39173858 1.39027775 2.28923588 2.07641807 2.67002628 2.34187563.4302146-.2452108 1.3086162-.74238132 1.5972981-.89423205.5447887-.28682915 1.0907006-.31944893 1.4568885-.08661115.3459689.2182151 3.3383754 2.21027167 3.6225641 2.41611376.2695862.19234426.4144887.5399137.4144887.91672846 0 .2969525-.089862.61190215-.2808189.88523346";

    public const string PhoneLine =
        "M2 7.4A5.4 5.4 0 0 1 7.4 2c.36 0 .7.22.83.55l1.93 4.64a1 1 0 0 1-.43 1.25L7 10a8.52 8.52 0 0 0 7 7l1.12-2.24a1 1 0 0 1 1.19-.51l5.06 1.56c.38.11.63.46.63.85C22 19.6 19.6 22 16.66 22h-.37C8.39 22 2 15.6 2 7.71V7.4Z" +
        "M13 3a1 1 0 0 1 1-1 8 8 0 0 1 8 8 1 1 0 1 1-2 0 6 6 0 0 0-6-6 1 1 0 0 1-1-1Z" +
        "M13 7a1 1 0 0 1 1-1 4 4 0 0 1 4 4 1 1 0 1 1-2 0 2 2 0 0 0-2-2 1 1 0 0 1-1-1Z";
    public const string VideoLine =
        "M4 4a3 3 0 0 0-3 3v10a3 3 0 0 0 3 3h11a3 3 0 0 0 3-3v-2.12a1 1 0 0 0 .55.9l3 1.5a1 1 0 0 0 1.45-.9V7.62a1 1 0 0 0-1.45-.9l-3 1.5a1 1 0 0 0-.55.9V7a3 3 0 0 0-3-3H4Z";

    // Screen-share: a monitor with an upward arrow inside (Discord's screenshare glyph).
    public const string MonitorLine =
        "M10 13h4v-2h-2l4.9-4.9L15.6 4 10 9.6V7H8v8h2v-2ZM18.5 2.001a2.5 2.5 0 0 1 2.5 2.5v8.493l-2-2.026V4.5a.5.5 0 0 0-.5-.5H4.5a.5.5 0 0 0-.5.5v9.945a.5.5 0 0 0 .5.5h8.243l1.977 2H4.5a2.5 2.5 0 0 1-2.5-2.5V4.5a2.5 2.5 0 0 1 2.5-2.5h14Z";

    /// The constants above that are stroke centre-lines rather than filled outlines.
    ///
    /// Call sites that draw an icon chosen at runtime — the chat header's button row, the message
    /// hover toolbar, the composer, the account tray — cannot know which kind they have, so they go
    /// through Draw. As each stand-in is replaced with real Discord geometry this set shrinks, and
    /// when it is empty both it and Draw can go.
    static readonly HashSet<string> Stroked = new()
    {
        GifBox, PencilLine, FileLine, Megaphone,
    };

    /// Paint an icon the way its geometry wants to be painted.
    public static void Draw(System.Drawing.Graphics g, string d, System.Drawing.RectangleF box,
                            System.Drawing.Color c, float strokeWidth = 1.9f)
    {
        if (d == MicLine || d == MicMutedLine)
        {
            Svg.SvgFill(g, MicBody, box, c, MicViewBox);
            Svg.SvgStroke(g, MicArc, box, c, 1.7f, MicViewBox);
            if (d == MicMutedLine) Svg.SvgFill(g, Slash, box, c);
            return;
        }
        if (Stroked.Contains(d)) Svg.SvgStroke(g, d, box, c, strokeWidth);
        else Svg.SvgFill(g, d, box, c);
    }
}
