﻿using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ReactiveUI.Fody
{
    public class ReactiveUIPropertyWeaver
    {
        public ModuleDefinition ModuleDefinition { get; set; }

        // Will log an MessageImportance.High message to MSBuild. OPTIONAL
        public Action<string> LogInfo  { get; set; }

        // Will log an error message to MSBuild. OPTIONAL
        public Action<string> LogError { get; set; }

        public void Execute()
        {
            var reactiveUI = ModuleDefinition.AssemblyReferences.Single(x => x.Name == "ReactiveUI");
            var reactiveObject = new TypeReference("ReactiveUI", "ReactiveObject", ModuleDefinition, reactiveUI);
            var targetTypes = ModuleDefinition.Types.Where(x => x.BaseType != null && reactiveObject.IsAssignableFrom(x.BaseType));
            var reactiveObjectExtensions = new TypeReference("ReactiveUI", "IReactiveObjectExtensions", ModuleDefinition, reactiveUI).Resolve();
            if (reactiveObjectExtensions == null)
                throw new Exception("reactiveObjectExtensions is null");

            var raiseAndSetIfChangedMethod = ModuleDefinition.Import(reactiveObjectExtensions.Methods.Single(x => x.Name == "RaiseAndSetIfChanged"));
            if (raiseAndSetIfChangedMethod == null)
                throw new Exception("raiseAndSetIfChangedMethod is null");

            var dataMemberAttribute = ModuleDefinition.Import(typeof(DataMemberAttribute));
            if (dataMemberAttribute == null)
                throw new Exception("dataMemberAttribute is null");

            foreach (var targetType in targetTypes)
            {
                foreach (var property in targetType.Properties.Where(x => x.IsDefined(dataMemberAttribute)).ToArray())
                {
                    // Declare a field to store the property value
                    var field = new FieldDefinition("$" + property.Name, FieldAttributes.Private, property.PropertyType);
                    targetType.Fields.Add(field);

                    targetType.Methods.Remove(property.GetMethod);
                    targetType.Methods.Remove(property.SetMethod);

                    property.GetMethod.Body = new MethodBody(property.GetMethod);
                    property.GetMethod.Body.Emit(il =>
                    {
                        il.Emit(OpCodes.Ldarg_0);                                   // this
                        il.Emit(OpCodes.Ldfld, field);                              // pop -> this.$PropertyName
                        il.Emit(OpCodes.Ret);                                       // Return the field value that is lying on the stack
                    });
                    property.GetMethod.SemanticsAttributes = MethodSemanticsAttributes.Getter;
                    targetType.Methods.Add(property.GetMethod);

                    var genericRaiseAndSetIfChangedMethod = raiseAndSetIfChangedMethod.MakeGenericMethod(targetType, property.PropertyType);

                    property.SetMethod.Body = new MethodBody(property.SetMethod);
                    property.SetMethod.Body.Emit(il =>
                    {
                        il.Emit(OpCodes.Ldarg_0);                                   // this
                        il.Emit(OpCodes.Ldarg_0);                                   // this
                        il.Emit(OpCodes.Ldflda, field);                             // pop -> this.$PropertyName
                        il.Emit(OpCodes.Ldarg_1);                                   // value
                        il.Emit(OpCodes.Ldstr, property.Name);                      // "PropertyName"
                        il.Emit(OpCodes.Call, genericRaiseAndSetIfChangedMethod);   // pop * 4 -> this.RaiseAndSetIfChanged(this.$PropertyName, value, "PropertyName")
                        il.Emit(OpCodes.Pop);                                       // We don't care about the result of RaiseAndSetIfChanged, so pop it off the stack (stack is now empty)
                        il.Emit(OpCodes.Ret);                                       // Return out of the function
                    });
                    property.SetMethod.SemanticsAttributes = MethodSemanticsAttributes.Setter;
                    targetType.Methods.Add(property.SetMethod);
                }
            }
        }         
    }
}