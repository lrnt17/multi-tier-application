using Microsoft.EntityFrameworkCore;
using MultiTierApplication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TodoDb>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var app = builder.Build();

//app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/healthz", () => Results.StatusCode(500));

// Returns all todos from the database
app.MapGet("/todos", async (TodoDb db) =>
    await db.Todos.ToListAsync());

// Returns a specific todo by id from the database
app.MapGet("/todos/{id}", async (int id, TodoDb db) =>
    await db.Todos.FindAsync(id)
        is Todo todo
            ? Results.Ok(todo)
            : Results.NotFound());

// Creates a new todo in the database
app.MapPost("/todos", async (Todo todo, TodoDb db) =>
{
    db.Todos.Add(todo);
    await db.SaveChangesAsync();
    return Results.Created($"/todos/{todo.Id}", todo);
});

// Deletes a specific todo by id from the database
app.MapDelete("/todos/{id}", async (int id, TodoDb db) =>
{
    if (await db.Todos.FindAsync(id) is Todo todo)
    {
        db.Todos.Remove(todo);
        await db.SaveChangesAsync();
        return Results.Ok(todo);
    }
    return Results.NotFound();
});

// Runs the application
app.Run();