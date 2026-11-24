# InfiniFrame repro: a custom scheme and web messaging as an application's API transport

We host a web application in an InfiniFrame window and want **no local HTTP port**: the application's files are served
by a custom scheme, and its API calls (JSON commands and results) go over InfiniFrame's web messaging. This repro shows
what works and the limits we met.

- **Measured with:** InfiniFrame 0.62.1 (`InfiniLore.InfiniFrame` and `InfiniLore.InfiniFrame.Js`), Windows 10 with
  WebView2, .NET 10.
- **Source reading:** the `core` branch on GitHub, not the 0.62.1 tag.
- **Not tested:** macOS, Linux.

## Run it

```
dotnet run              # the scheme "app"
dotnet run -- ckapp     # any other scheme name
```

The window opens, the page runs its tests and closes the window. The results are printed in the console:
- **`PAGE:`** a line reported by the page;
- **`HOST:`** what the .NET side received;
- **`SCHEME HANDLER:`** what the scheme handler received.

The page reports each line by loading an image `<scheme>://localhost/log/N?text`. This side channel goes through the
scheme handler, so it works even when `fetch` doesn't.

- **The scheme handler** (`Program.cs`) serves `www/`, with a fallback to `index.html` for deep links. It answers
  `/api/*` to show what it receives.
- **Two `RegisterWebMessagePostHandler`** echo the requests `{ id, body }`; the replies are sent from the thread pool.
  A body `"BIG:n"` is answered with n characters.
- **The page tests two transports:** InfiniFrame.js (`messaging.sendMessageToHost` and
  `messaging.assignMessageReceivedHandler`), and the same envelopes posted straight to `chrome.webview.postMessage`, with
  the replies read from the `message` event of `chrome.webview`.

## The results (Windows)

| | scheme `app` | scheme `ckapp` |
|---|---|---|
| `location.origin` | `app://localhost` | `null` |
| `fetch` GET `/data.json` | 200 | `TypeError: Failed to fetch` |
| `localStorage` | works | `SecurityError: Access is denied for this document` |
| deep link (`/deep/route`) | served (handler fallback) | served (handler fallback) |
| `fetch` POST `/api/echo` with a body | 200, but the handler gets **the URL only** | `Failed to fetch` |
| web messaging (envelopes) | works | works |

Messaging, the same with both schemes:

| | result |
|---|---|
| small request, InfiniFrame.js | ~30 ms |
| 512 KB request, InfiniFrame.js | ~30 ms |
| 100 concurrent requests, InfiniFrame.js | all matched, ~7 ms |
| **1 MiB request, InfiniFrame.js** | **no answer** (rejected, silently) |
| page -> host 0.99 MB, direct | ok, ~55 ms |
| **page -> host 1.01 MB, direct** | **never reaches the host** (silently) |
| host -> page 2 MB, direct | ok, ~60-90 ms |
| host -> page 16 MB, direct | ok, ~440 ms |

Also observed:
- **A raw string** posted by the page (`chrome.webview.postMessage( "text" )`) never reaches
  `RegisterWebMessageReceivedHandler`. Only an envelope `{ id, command: "Post", version: 2, data }` reaches the
  `RegisterWebMessagePostHandler( id, ... )` of its id. This was not obvious from the API names.

## What we found in the source (core branch)

- **Only a scheme named `app` gets a usable origin.**
  - `WebView2CustomSchemes.Win32.cpp` calls `put_HasAuthorityComponent(TRUE)`, `put_TreatAsSecure(TRUE)` and
    `SetAllowedOrigins({"app://localhost"})` only when the scheme name is `app`.
  - `WebKitCustomSchemes.Gtk.cpp` registers only `app` as secure and CORS enabled.
  - `WebKitCustomSchemes.Cocoa.mm` sets no flag for any scheme.
- **The scheme handlers forward the URI only** (Windows `WebResourceRequested`, GTK and Cocoa alike). The `Origin` header
  is read only to build the CORS headers of the response. The method, the other headers and the body are not forwarded.
- **The 1 MiB cap is `InteropMessageMaxSizeBytes = 1048576`.** The JS envelope parser enforces it, and the measures show
  it is also enforced on the host side for the page -> host direction. WebView2 itself shows no such limit: 16 MB passes
  host -> page.

## Our requests

1. **Make the message size cap configurable**, for example as an option of the window builder, applied by the host and
   by the JS bridge (it could be given to the page with the ready handshake).
2. **Log a rejected message** (too large, not an envelope) instead of dropping it silently. Today the caller only sees a
   timeout.
3. **Expose the scheme registration flags per scheme** (secure, authority, allowed origins), or document that only `app`
   gets them. Today another name silently gives a page with a `null` origin.
4. *(Optional)* **Forward the method, the headers and the body to the scheme handler**, and let it set the status code
   and the headers. A scheme could then serve an API too.
