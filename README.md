# TwitterMonitor

This is a simple azure function which emails newly posted tweets (as per specified search) on a timer trigger.

I wrote this so I don't need to always be reading my twitter feed, but will be notified within minutes if any tweet gets posted which I'm interested in.

*Details*

State is stored in a blob (see the read and write parameters), but this can definitely be improved by using a database or similar...you could even store which tweet was sent when, etc.

Also, I wanted to use the SendGrid output, but it seems there are some nuget incompatibilities that I couldn't easily get around (runtime errors around parameter binding), so I'm just using the SendGrid client directly.
