// Repro: a custom scheme and web messaging as an application's API transport (InfiniFrame 0.62.1).
// Usage: dotnet run                 -> scheme "app"   (works: origin app://localhost)
//        dotnet run -- ckapp        -> scheme "ckapp" (origin null: fetch, localStorage and messaging fail)
// The page reports each result by loading an image "<scheme>://localhost/log/N?text": this side channel goes through the
// scheme handler, so it works even when the origin is null and the messaging doesn't.
using InfiniFrame;
using InfiniFrame.Security;
using System.Text.Json;

var scheme = args.Length > 0 ? args[0] : "app";
var root = Path.Combine( AppContext.BaseDirectory, "www" );
Console.WriteLine( $"InfiniFrame repro, scheme '{scheme}'." );

(Stream, string) HandleScheme( IInfiniFrameWindow w, string url )
{
    var uri = new Uri( url );
    var path = uri.AbsolutePath.TrimStart( '/' );
    if( path.StartsWith( "log/" ) )
    {
        var text = Uri.UnescapeDataString( uri.Query.TrimStart( '?' ) );
        Console.WriteLine( $"  PAGE: {text}" );
        if( text == "DONE" ) Task.Run( async () => { await Task.Delay( 300 ); w.Close(); } );
        return (new MemoryStream(), "image/gif");
    }
    if( path.StartsWith( "api/" ) )
    {
        // The handler only gets the URL: the method, the headers and the body of a POST are not available.
        Console.WriteLine( $"  SCHEME HANDLER: '{url}' (this is all the handler receives)" );
        return (new MemoryStream( "{\"handlerReceivedOnly\":\"the url\"}"u8.ToArray() ), "application/json");
    }
    var file = Path.Combine( root, path );
    if( path.Length == 0 || !File.Exists( file ) ) file = Path.Combine( root, "index.html" ); // SPA fallback (deep links).
    var contentType = file.EndsWith( ".js" ) ? "text/javascript" : file.EndsWith( ".json" ) ? "application/json" : "text/html";
    return (File.OpenRead( file ), contentType);
}

// Echoes a request { id, body } as { id, body } in an envelope of the same id. A body "BIG:n" is answered with n characters.
void Echo( IInfiniFrameWindow w, string envelopeId, string payload )
{
    if( payload.Length > 1000 ) Console.WriteLine( $"  HOST received '{envelopeId}': {payload.Length} chars." );
    var m = JsonDocument.Parse( payload ).RootElement;
    var id = m.GetProperty( "id" ).GetInt32();
    var body = m.GetProperty( "body" ).GetString()!;
    if( body.StartsWith( "BIG:" ) ) body = new string( 'z', int.Parse( body[4..] ) );
    // Replied asynchronously, from the thread pool (as a real API call would be).
    Task.Run( async () =>
    {
        var data = JsonSerializer.Serialize( new { id, body } );
        var envelope = JsonSerializer.Serialize( new { id = envelopeId, command = "Post", version = 2, data } );
        await w.SendWebMessageAsync( envelope, default );
    } );
}

void RunWindow()
{
    var window = InfiniFrameWindowBuilder.Create()
                    .SetTitle( $"InfiniFrame repro ({scheme})" )
                    .SetSize( 900, 600 )
                    .RegisterCustomSchemeHandler( scheme, HandleScheme )
                    // A raw (non envelope) string posted by the page never reaches this handler.
                    .RegisterWebMessageReceivedHandler( ( IInfiniFrameWindow w, string raw ) => Console.WriteLine( $"  HOST RegisterWebMessageReceivedHandler: {raw[..Math.Min( 80, raw.Length )]}" ) )
                    .RegisterWebMessagePostHandler( "repro:js", ( IInfiniFrameWindow w, string? payload ) => Echo( w, "repro:js", payload! ) )
                    .RegisterWebMessagePostHandler( "repro:direct", ( IInfiniFrameWindow w, string? payload ) => Echo( w, "repro:direct", payload! ) )
                    .SetStartPageContent( "<html><body>starting...</body></html>" )
                    .Build();
    // InfiniFrame only navigates to a trusted origin.
    var policy = InfiniFrameUriSecurityPolicyRegistry.GetForWindow( window );
    InfiniFrameUriSecurityPolicyRegistry.BindToWindow( window, new InfiniFrameUriSecurityPolicyBuilder( policy )
                                                                    .AllowNavigationScheme( scheme )
                                                                    .AddTrustedOrigin( new Uri( $"{scheme}://localhost" ) )
                                                                    .Build() );
    Task.Run( async () => { await Task.Delay( 1000 ); window.Load( new Uri( $"{scheme}://localhost/deep/route" ) ); } );
    window.WaitForClose();
}

var t = new Thread( RunWindow );
t.SetApartmentState( ApartmentState.STA );
t.Start();
if( !t.Join( TimeSpan.FromMinutes( 3 ) ) )
{
    Console.WriteLine( "The page didn't finish in 3 minutes." );
    Environment.Exit( 1 );
}
