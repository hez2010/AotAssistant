# Assistant for .NET NativeAOT

This utility can auto generate runtime directives for apps on-the-fly.

## Usage

Publish your app with ReadyToRun so that all methods that are not being called dynamically can be AOT-ed.

Then run the published artifact with this tool:

```pwsh
./AotAssistant <path/to/exe> args...
```

After complete running your app, you can press `Ctrl+C` to stop the process and get the generated runtime directives.

Note that the generated runtime directives are not completed and which can also contains some implementation details in CoreCLR that NativeAOT doesn't have.

You may still need to add or remove some entries from the generated runtime directives until it works. 

## Example
`Test.cs`:

```cs
using System.Reflection;

class Program
{
    static void Main(string[] args)
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            var type = Type.GetType(line)!;
            var method = type.GetMethod(Console.ReadLine()!, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var instantiation = Type.GetType(Console.ReadLine()!)!;
            var instantiatedMethod = method.MakeGenericMethod(instantiation);
            instantiatedMethod.Invoke(Activator.CreateInstance(type), null);
        }
    }
}

class Foo<T>
{
    void Print<U>()
    {
        Console.WriteLine($"Called Foo<{typeof(T)}>.Print<{typeof(U)}>");
    }
}
```

Publish with ReadyToRun with composite mode:

```pwsh
dotnet publish -c Release -r win-x64 /p:PublishReadyToRun=true /p:PublishReadyToRunComposite=true
```

Run the assistant:

```pwsh
./AotAssistant.exe Test.exe
```

Type something in the console:

```
Foo`1[System.Int32]
Print
System.Single
Foo`1[System.Int64]
Print
System.String
Foo`1[System.String]
Print
System.Int32
```

Get output from stdout:

```
Called Foo<System.Int32>.Print<System.Single>
Called Foo<System.Int64>.Print<System.String>
Called Foo<System.String>.Print<System.Int32>
```

Generated runtime directives:

```xml
<Directives>
    <Application>
        ...
        <Assembly Name="Test">
            <Type Name="Foo`1[[System.Int32, System.Private.CoreLib]]">
                <Method Name=".ctor" />
                <Method Name="Print">
                    <GenericArgument Name="System.Single, System.Private.CoreLib" />
                </Method>
            </Type>
            <Type Name="Foo`1[[System.Int64, System.Private.CoreLib]]">
                <Method Name=".ctor" />
                <Method Name="Print">
                    <GenericArgument Name="System.Object, System.Private.CoreLib" />
                </Method>
            </Type>
            <Type Name="Foo`1[[System.Object, System.Private.CoreLib]]">
                <Method Name="Print">
                    <GenericArgument Name="System.Int32, System.Private.CoreLib" />
                </Method>
            </Type>
        </Assembly>
    </Application>
</Directives>
```