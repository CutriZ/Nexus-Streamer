using System.Collections.ObjectModel;

namespace StreamingDoc.App.Model;

/// <summary>Scena = lista ordinata di scene item. Indice 0 = sotto, ultimo = sopra (z-order).</summary>
public sealed class Scene
{
    public string Name { get; set; }
    public ObservableCollection<SceneItem> Items { get; } = new();

    public Scene(string name) => Name = name;

    public override string ToString() => Name;
}
