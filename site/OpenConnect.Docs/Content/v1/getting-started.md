---
title: Getting started
version: v1
summary: Create a small ASP.NET Core application with OpenConnect.
order: 10
---

# Getting started

OpenConnect is a .NET-first application framework around Microsoft Agent Framework. Start with a configured model and the trusted host boundary.

## Register the framework

```csharp
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true;
});
```

Registration does not start Docker, download models, create databases or provision NATS streams.

## Continue

Read the [Todo sample tutorial](/tutorials/todo) to see the maintained reference application.
