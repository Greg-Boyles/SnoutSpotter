using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class InferenceStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
    public required Table ClipsTable { get; init; }
}

public class InferenceStack : Stack
{
    public InferenceStack(Construct scope, string id, InferenceStackProps props) : base(scope, id, props)
    {
        var inferenceFunction = new Function(this, "RunInferenceFunction", new FunctionProps
        {
            FunctionName = "snout-spotter-run-inference",
            Runtime = Runtime.DOTNET_8,
            Handler = "SnoutSpotter.Lambda.RunInference::SnoutSpotter.Lambda.RunInference.Function::FunctionHandler",
            Code = Code.FromAsset("../lambdas/SnoutSpotter.Lambda.RunInference/bin/Release/net8.0/publish"),
            MemorySize = 2048,
            Timeout = Duration.Minutes(5),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["TABLE_NAME"] = props.ClipsTable.TableName,
                ["DETECTOR_MODEL_KEY"] = "models/dog-detector/best.onnx",
                ["CLASSIFIER_MODEL_KEY"] = "models/dog-classifier/best.onnx"
            }
        });

        // Grant permissions
        props.DataBucket.GrantRead(inferenceFunction);
        props.ClipsTable.GrantReadWriteData(inferenceFunction);
    }
}
