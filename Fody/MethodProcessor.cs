using System;
using System.Globalization;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using NullGuard;

public class MethodProcessor
{
    public ModuleWeaver ModuleWeaver;
    public MethodDefinition Method;
    private MethodBody body;

    public void Process()
    {
        try
        {
            InnerProcess();
        }
        catch (Exception exception)
        {
            throw new WeavingException(string.Format("An error occurred processing '{0}'. Error: {1}", Method.FullName, exception.Message));
        }
    }

    private void InnerProcess()
    {
        var validationFlags = this.ModuleWeaver.ValidationFlags;

        if (Method.DeclaringType.IsCustomAttributeDefined<NullGuardAttribute>())
        {
            var attribute = Method.DeclaringType.GetCustomAttribute<NullGuardAttribute>();
            if (attribute != null)
                validationFlags = (ValidationFlags)attribute.ConstructorArguments[0].Value;
        }

        if ((!validationFlags.HasFlag(ValidationFlags.NonPublic) && !Method.IsPublic)
            || Method.IsProperty()
            )
            return;

        body = Method.Body;
        body.SimplifyMacros();

        if (validationFlags.HasFlag(ValidationFlags.Arguments))
        {
            InjectMethodArgumentGuards(validationFlags);
        }

        if (validationFlags.HasFlag(ValidationFlags.ReturnValues) &&
            !Method.MethodReturnType.AllowsNull() &&
            !Method.ReturnType.IsValueType &&
            Method.ReturnType.FullName != typeof(void).FullName)
        {
            InjectMethodReturnGuard(validationFlags);
        }

        body.InitLocals = true;
        body.OptimizeMacros();
    }

    private void InjectMethodArgumentGuards(ValidationFlags validationFlags)
    {
        var newArgumentNullExceptionCtor = ModuleWeaver.ModuleDefinition.Import(typeof(ArgumentNullException).GetConstructor(new Type[] { typeof(string) }));
        var newInvalidOperationExceptionCtor = ModuleWeaver.ModuleDefinition.Import(typeof(InvalidOperationException).GetConstructor(new Type[] { typeof(string) }));

        int i = Method.Parameters.Count;
        foreach (var parameter in Method.Parameters.Reverse())
        {
            i--;

            if (parameter.MayNotBeNull())
            {
                var entry = body.Instructions.First();

                body.Instructions.Prepend(

                    // Load the argument onto the stack
                    Instruction.Create(OpCodes.Ldarg, parameter),

                    // Branch if value on stack is true, not null or non-zero
                    Instruction.Create(OpCodes.Brtrue_S, entry),

                    // Load the name of the argument onto the stack
                    Instruction.Create(OpCodes.Ldstr, parameter.Name),

                    // Load the ArgumentNullException onto the stack
                    Instruction.Create(OpCodes.Newobj, newArgumentNullExceptionCtor),

                    // Throw the top item of the stack
                    Instruction.Create(OpCodes.Throw)
                    );
            }

            if (validationFlags.HasFlag(ValidationFlags.OutValues) && parameter.IsOut && !parameter.ParameterType.IsValueType)
            {
                var returnPoints = body.Instructions
                    .Select((o, ix) => new { o, ix })
                    .Where(a => a.o.OpCode == OpCodes.Ret)
                    .Select(a => a.ix)
                    .OrderByDescending(ix => ix);

                foreach (var ret in returnPoints)
                {
                    var returnInstruction = body.Instructions[ret];

                    body.Instructions.Insert(ret,

                        // Load the out parameter onto the stack
                        Instruction.Create(OpCodes.Ldarg, parameter),

                        // Not really sure what this is for. *jazz hands*
                        Instruction.Create(OpCodes.Ldind_Ref),

                        // Branch if value on stack is true, not null or non-zero
                        Instruction.Create(OpCodes.Brtrue_S, returnInstruction),

                        // Load the exception text onto the stack
                        Instruction.Create(OpCodes.Ldstr, String.Format(CultureInfo.InvariantCulture, "Out parameter '{0}' is null.", parameter.Name)),

                        // Load the InvalidOperationException onto the stack
                        Instruction.Create(OpCodes.Newobj, newInvalidOperationExceptionCtor),

                        // Throw the top item of the stack
                        Instruction.Create(OpCodes.Throw)
                        );
                }
            }
        }
    }

    private void InjectMethodReturnGuard(ValidationFlags validationFlags)
    {
        var newInvalidOperationExceptionCtor = ModuleWeaver.ModuleDefinition.Import(typeof(InvalidOperationException).GetConstructor(new Type[] { typeof(string) }));

        var returnPoints = body.Instructions
                .Select((o, ix) => new { o, ix })
                .Where(a => a.o.OpCode == OpCodes.Ret)
                .Select(a => a.ix)
                .OrderByDescending(ix => ix);

        foreach (var ret in returnPoints)
        {
            var returnInstruction = body.Instructions[ret];

            body.Instructions.Insert(ret,

                // Duplicate the stack (this should be the return value)
                Instruction.Create(OpCodes.Dup),

                // Branch if value on stack is true, not null or non-zero
                Instruction.Create(OpCodes.Brtrue_S, returnInstruction),

                // Clean up the stack since we're about to throw up.
                Instruction.Create(OpCodes.Pop),

                // Load the exception text onto the stack
                Instruction.Create(OpCodes.Ldstr, String.Format(CultureInfo.InvariantCulture, "Return value of method '{0}' is null.", Method.Name)),

                // Load the InvalidOperationException onto the stack
                Instruction.Create(OpCodes.Newobj, newInvalidOperationExceptionCtor),

                // Throw the top item of the stack
                Instruction.Create(OpCodes.Throw)
                );
        }
    }
}